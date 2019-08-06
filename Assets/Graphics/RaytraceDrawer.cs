using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using Random = UnityEngine.Random;

[RequireComponent(typeof(Camera))]
public class RaytraceDrawer : MonoBehaviour {

    public List<SphereData> Spheres;
    public List<LightingData> Lights;
    public List<SingleTriangleData> Triangles;

    public enum SuperSampleAmount {
        x1 = 1,
        x2 = 2,
        x4 = 4,
        x8 = 8,
        x16 = 16
    }

    public SuperSampleAmount SuperSample;

    public Color BackgroundColor;

    public Transform ViewportTransform;
    public Vector2 ViewportSize;

    private RaytraceSystemUsingParallelFor raytraceSystem;

    void Awake() {
        // Set raytrace attributes
        raytraceSystem = World.Active.GetExistingSystem<RaytraceSystemUsingParallelFor>();
        raytraceSystem.Cam = GetComponent<Camera>();
        raytraceSystem.drawer = this;
    }

    void OnValidate() {
        if(raytraceSystem != null) raytraceSystem.Dirty = true;
    }

    void Update() {
        raytraceSystem.ViewportPosition = ViewportTransform.position;
        raytraceSystem.ViewportSize = ViewportSize;
    }

    private void OnRenderImage(RenderTexture source, RenderTexture destination) {
        if(raytraceSystem.RenderedResult != null)
            Graphics.Blit(raytraceSystem.RenderedResult, destination);
        else
            Graphics.Blit(source, destination);

    }
}
