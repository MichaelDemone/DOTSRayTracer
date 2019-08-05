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
    [BurstCompile]
    struct RaytraceSystemJob : IJobParallelFor {

        [ReadOnly] public NativeArray<SphereData> Spheres;
        [ReadOnly] public NativeArray<LightingData> Lights;
        [WriteOnly] public NativeArray<Color> PixelColors;

        public int2 TextureSize;

        public float2 ViewportSize;
        public float3 ViewportPosition;
        public float3 CameraPosition;

        public void Execute(int index) {

            
            int x = index % TextureSize.x;
            int y = index / TextureSize.x;

            PixelColors[index] = new Color(x, x, x, 1);

            // Find pixel position in world space
            // Assuming pixel (0, 0) is in bottom left
            // As you go across the texture, you're moving in the positive x direction in virtual world space
            float3 pixelPos;
            pixelPos.x = ViewportPosition.x - (ViewportSize.x / 2) + (ViewportSize.x / TextureSize.x) * x;
            // As you go up the texture, you're moving in the positive y direction in virtual world space
            pixelPos.y = ViewportPosition.y - (ViewportSize.y / 2) + (ViewportSize.y / TextureSize.y) * y;
            pixelPos.z = ViewportPosition.z;
            
            // Create ray from camera position to pixel position
            // cameraPos + t * d
            float3 d = normalize(pixelPos - CameraPosition);

            // Loop through spheres to see if any intersect
            float3 e = CameraPosition;

            
            for(int i = 0; i < Spheres.Length; i++) { // No foreachs in jobs
                SphereData sphere = Spheres[i];
                // Using ray-sphere intersection equation
                float3 c = sphere.Position;
                float R = sphere.Radius;
                float3 eminusc = e - c;
                float ddoteminusc = dot(d, eminusc);
                float descriminant = ddoteminusc * ddoteminusc - dot(d, d) * (dot(eminusc, eminusc) - R * R);
                if (descriminant < 0) {
                    // No hit
                    continue;
                }
                float sqrtdesc = sqrt(descriminant);
                float plust = (-ddoteminusc + sqrtdesc) / dot(d, d);
                float minust = (-ddoteminusc - sqrtdesc) / dot(d, d);

                if (plust > 0 && minust > 0) {
                    // In front of camera
                    PixelColors[index] = new Color32(0,0,0,0);

                    float t = min(plust, minust);

                    // p = intersect point
                    float3 p = e + t * d;
                    // n = surface normal
                    float3 n = (p - c) / R;

                    // v = direction from point to viewers eye
                    float3 v = normalize(e - p);

                    Color color = new Color(0, 0, 0, 1);

                    // It's been hit! Calculate the color based on lighting
                    for(int j = 0; j < Lights.Length; j++) {
                        LightingData light = Lights[j];

                        // l = vector from light to intersect point
                        float3 l = normalize(light.Position - p);

                        // h = half vector to light source
                        float3 h = normalize(v + l);
                        color += sphere.diffuseCoefficient * light.Color * max(0, dot(n, l)) + sphere.specularCoefficient * light.Color * pow(max(0, dot(n, h)), sphere.phongExponent);
                    }

                    PixelColors[index] = color;

                } else if (plust > 0 && minust < 0 || plust < 0 && minust > 0) {
                    // Inside circle
                    
                } else if (plust < 0 && minust < 0) {
                    // Circle is behind camera.

                }
            }
            
        }
    }

    public NativeArray<SphereData> Spheres;
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

        // Sphere and Lighting update
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

            if (Dirty) {
                for(int i = 0; i < drawer.Spheres.Count; i++) {
                    Spheres[i] = drawer.Spheres[i];
                }
                for(int i = 0; i < drawer.Lights.Count; i++) {
                    Lights[i] = drawer.Lights[i];
                }
                Dirty = false;
            }
        }

        

        var job = new RaytraceSystemJob();

        job.Spheres = Spheres;
        job.Lights = Lights;
        job.PixelColors = PixelColors;
        job.TextureSize = new int2(Cam.pixelWidth, Cam.pixelHeight);
        job.ViewportSize = new float2(ViewportSize.x, ViewportSize.y);
        job.ViewportPosition = new float3(ViewportPosition.x, ViewportPosition.y, ViewportPosition.z);
        job.CameraPosition = new float3(Cam.transform.position.x, Cam.transform.position.y, Cam.transform.position.z);
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
    }
}

[Serializable]
public struct SphereData {
    public float3 Position;
    public float Radius;
    public Color diffuseCoefficient;
    public Color specularCoefficient;
    public float phongExponent;
}


[Serializable]
public struct LightingData {
    public float3 Position;
    public Color Color;
}