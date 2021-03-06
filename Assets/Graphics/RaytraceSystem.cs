using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using static Unity.Mathematics.math;

public class RaytraceSystemUsingParallelFor : JobComponentSystem {

    public NativeArray<SphereData> Spheres;
    public NativeArray<SingleTriangleData> Triangles;
    public NativeArray<LightingData> Lights;
    public NativeArray<Color> PixelColors;
    public Vector3 ViewportPosition;
    public Vector2 ViewportSize;
    public Camera Cam;
    public Texture2D RenderedResult = null;
    public RaytraceDrawer drawer;
    public bool Dirty;

    protected override JobHandle OnUpdate(JobHandle inputDependencies) {

        if (!PixelColors.IsCreated || Cam.pixelHeight * Cam.pixelWidth != PixelColors.Length) {
            if(PixelColors.IsCreated) PixelColors.Dispose();

            PixelColors = new NativeArray<Color>(Cam.pixelHeight * Cam.pixelWidth, Allocator.Persistent);
            RenderedResult = new Texture2D(Cam.pixelWidth, Cam.pixelHeight, TextureFormat.RGBAFloat, false);
        }

        // Sphere, Triangles, and Lighting update
        {
            if (Spheres.Length != drawer.Spheres.Count) {
                if(Spheres.IsCreated) Spheres.Dispose();
                Spheres = new NativeArray<SphereData>(drawer.Spheres.Count, Allocator.Persistent);
                Dirty = true;
            }

            if(Lights.Length != drawer.Lights.Count) {
                if(Lights.IsCreated)
                    Lights.Dispose();
                Lights = new NativeArray<LightingData>(drawer.Lights.Count, Allocator.Persistent);
                Dirty = true;
            }

            if(Triangles.Length != drawer.Triangles.Count) {
                if(Triangles.IsCreated)
                    Triangles.Dispose();
                Triangles = new NativeArray<SingleTriangleData>(drawer.Triangles.Count, Allocator.Persistent);
                Dirty = true;
            }

            if (Dirty) {
                for(int i = 0; i < drawer.Spheres.Count; i++) {
                    Spheres[i] = drawer.Spheres[i];
                }
                for(int i = 0; i < drawer.Lights.Count; i++) {
                    Lights[i] = drawer.Lights[i];
                }
                for(int i = 0; i < drawer.Triangles.Count; i++) {
                    Triangles[i] = drawer.Triangles[i];
                }
                Dirty = false;
            }
        }

        var job = new RaytraceSystemJob();

        job.Spheres = Spheres;
        job.Lights = Lights;
        job.Triangles = Triangles;
        job.PixelColors = PixelColors;
        job.TextureSize = new int2(Cam.pixelWidth, Cam.pixelHeight);
        job.ViewportSize = new float2(ViewportSize.x, ViewportSize.y);
        job.ViewportPosition = new float3(ViewportPosition.x, ViewportPosition.y, ViewportPosition.z);
        job.CameraPosition = new float3(Cam.transform.position.x, Cam.transform.position.y, Cam.transform.position.z);
        job.BackgroundColor = drawer.BackgroundColor;
        job.SuperSamplingDegree = (int) drawer.SuperSample;
        RenderedResult.LoadRawTextureData(PixelColors);
        RenderedResult.Apply();

        // Now that the job is set up, schedule it to be run. 
        return job.Schedule(Cam.pixelHeight * Cam.pixelWidth, 100, inputDependencies);
    }

    protected override void OnCreate() {
        
    }

    protected override void OnDestroy() {
        Spheres.Dispose();
        Lights.Dispose();
        PixelColors.Dispose();
        Triangles.Dispose();
    }

    /// <summary>
    /// The job for calculating absolutely everything.
    /// </summary>
    [BurstCompile]
    struct RaytraceSystemJob : IJobParallelFor {

        // Output of the job
        [WriteOnly] public NativeArray<Color> PixelColors;

        // Data required to do the job
        [ReadOnly] public NativeArray<SphereData> Spheres;
        [ReadOnly] public NativeArray<SingleTriangleData> Triangles;
        [ReadOnly] public NativeArray<LightingData> Lights;
        [ReadOnly] public int2 TextureSize;
        [ReadOnly] public float2 ViewportSize;
        [ReadOnly] public float3 ViewportPosition;
        [ReadOnly] public float3 CameraPosition;
        [ReadOnly] public Color BackgroundColor;

        // Can be 1, 2, 4, 8, 16
        [ReadOnly] public int SuperSamplingDegree;

        public void Execute(int index) {

            
            float3 pixelPos;

            // Calculate the pixels position in the game world
            {
                int x = index % TextureSize.x;
                int y = index / TextureSize.x;

                // Assuming pixel (0, 0) is in bottom left
                // As you go across the texture, you're moving in the positive x direction in virtual world space
                pixelPos.x = ViewportPosition.x - (ViewportSize.x / 2) + (ViewportSize.x / TextureSize.x) * x;
                // As you go up the texture, you're moving in the positive y direction in virtual world space
                pixelPos.y = ViewportPosition.y - (ViewportSize.y / 2) + (ViewportSize.y / TextureSize.y) * y;
                pixelPos.z = ViewportPosition.z;
            }

            // Do super sampling by dividing pixel up into SuperSamplingDegree x SuperSamplingDegree pixels
            // then ray tracing each pixel and averaging them.
            float2 pixelSize = (ViewportSize / TextureSize);
            Color c = new Color(0, 0, 0, 1);

            for (int i = 0; i < SuperSamplingDegree; i++) {
                float x = pixelPos.x + i * (pixelSize.x / (SuperSamplingDegree * 2));
                for(int j = 0; j < SuperSamplingDegree; j++) {
                    float y = pixelPos.y + j * (pixelSize.y / (SuperSamplingDegree * 2));
                    float z = pixelPos.z;
                    c += GetColor(float3(x, y, z)) / (SuperSamplingDegree * SuperSamplingDegree);
                }
            }

            PixelColors[index] = c;
        }

        private Color GetColor(float3 pixelPos) {
            // Create ray from camera position to pixel position
            // e + t * d (where e is origin)
            float3 d = normalize(pixelPos - CameraPosition);
            float3 e = CameraPosition;

            // Find closest intersection point
            float closestt = 1000000;
            float3 closestIntersectionPoint = new float3(0, 0, 0);
            float3 closestIntersectionNormal = new float3(0, 0, 0);
            ObjectLightingData lightingData = new ObjectLightingData();

            // Loop Spheres
            for(int i = 0; i < Spheres.Length; i++) { // No foreachs in jobs
                SphereData sphere = Spheres[i];

                // Using ray-sphere intersection equation
                float3 c = sphere.Position;
                float R = sphere.Radius;

                float3 eminusc = e - c;
                float ddoteminusc = dot(d, eminusc);
                float descriminant = ddoteminusc * ddoteminusc - dot(d, d) * (dot(eminusc, eminusc) - R * R);
                if(descriminant < 0) {
                    // No hit
                    continue;
                }

                float sqrtdesc = sqrt(descriminant);
                float plust = (-ddoteminusc + sqrtdesc) / dot(d, d);
                float minust = (-ddoteminusc - sqrtdesc) / dot(d, d);

                if(plust > 0 && minust > 0) {
                    // In front of camera
                    float t = min(plust, minust);
                    if(t < closestt) {
                        closestIntersectionPoint = e + t * d;
                        closestIntersectionNormal = (closestIntersectionPoint - c) / R;
                        closestt = t;
                        lightingData = sphere.LightingData;
                    }
                } else if(plust > 0 && minust < 0 || plust < 0 && minust > 0) {
                    // Inside circle

                } else if(plust < 0 && minust < 0) {
                    // Circle is behind camera.
                }
            }

            // Loop Triangles
            for(int i = 0; i < Triangles.Length; i++) {
                // No foreachs in jobs
                SingleTriangleData triangle = Triangles[i];

                float3 a = triangle.a;
                float3 b = triangle.b;
                float3 c = triangle.c;

                float detA = determinant(
                    float3x3(a.x - b.x, a.x - c.x, d.x,
                        a.y - b.y, a.y - c.y, d.y,
                        a.z - b.z, a.z - c.z, d.z));

                // Compute t
                float t = determinant(
                              float3x3(a.x - b.x, a.x - c.x, a.x - e.x,
                                  a.y - b.y, a.y - c.y, a.y - e.y,
                                  a.z - b.z, a.z - c.z, a.z - e.z)) /
                          detA;

                if(t < 0 || t > closestt)
                    continue;

                // Compute gamma
                float gamma = determinant(
                                  float3x3(a.x - b.x, a.x - e.x, d.x,
                                      a.y - b.y, a.y - e.y, d.y,
                                      a.z - b.z, a.z - e.z, d.z)) /
                              detA;

                if(gamma < 0 || gamma > 1)
                    continue;

                // Compute beta
                float beta = determinant(
                                 float3x3(a.x - e.x, a.x - c.x, d.x,
                                     a.y - e.y, a.y - c.y, d.y,
                                     a.z - e.z, a.z - c.z, d.z)) /
                             detA;

                if(beta < 0 || beta > 1 - gamma)
                    continue;

                closestt = t;
                closestIntersectionPoint = e + t * d;
                closestIntersectionNormal = normalize(cross(c - a, b - a));
                lightingData = triangle.LightingData;
            }

            // Sphong Blinn Shading with no ambient lighting
            if(closestt < 1000000) {
                float3 p = closestIntersectionPoint;
                float3 n = closestIntersectionNormal;

                // v = direction from point to viewers eye
                float3 v = normalize(e - closestIntersectionPoint);

                Color color = new Color(0, 0, 0, 1);

                // It's been hit! Calculate the color based on lighting
                for(int j = 0; j < Lights.Length; j++) {
                    LightingData light = Lights[j];

                    // l = vector from light to intersect point
                    float3 l = normalize(light.Position - p);

                    // h = half vector to light source
                    float3 h = normalize(v + l);
                    color += lightingData.diffuseCoefficient * light.Color * max(0, dot(n, l)) +
                             lightingData.specularCoefficient * light.Color *
                             pow(max(0, dot(n, h)), lightingData.phongExponent);
                }

                return color;
            }
            return BackgroundColor;
        }
    }
}

[Serializable]
public struct ObjectLightingData {
    public Color diffuseCoefficient;
    public Color specularCoefficient;
    public float phongExponent;
}

[Serializable]
public struct SphereData {
    public float3 Position;
    public float Radius;
    public ObjectLightingData LightingData;
}

[Serializable]
public struct SingleTriangleData {
    public float3 a, b, c; // corners a, b, and c
    public ObjectLightingData LightingData;
}

[Serializable]
public struct LightingData {
    public float3 Position;
    public Color Color;
}