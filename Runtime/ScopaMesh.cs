using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using Sledge.Formats.Map.Formats;
using Sledge.Formats.Map.Objects;
using Sledge.Formats.Precision;
using Scopa.Formats.Texture.Wad;
using Scopa.Formats.Id;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine.Rendering;
using Mesh = UnityEngine.Mesh;
using Vector3 = UnityEngine.Vector3;

#if SCOPA_USE_BURST
using Unity.Burst;
using Unity.Mathematics;
#endif

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Scopa
{
    /// <summary>main class for Scopa mesh generation / geo functions</summary>
    public static class ScopaMesh
    {
        // to avoid GC, we use big static lists so we just allocate once
        // TODO: will have to reorganize this for multithreading later on
        static List<Face> allFaces = new List<Face>(8192);
        static HashSet<Face> discardedFaces = new HashSet<Face>(8192);

        static List<Vector3> verts = new List<Vector3>(4096);
        static List<Vector3> faceVerts = new List<Vector3>(64);
        static List<int> tris = new List<int>(8192);
        static List<int> faceTris = new List<int>(32);
        static List<Vector2> uvs = new List<Vector2>(4096);
        static List<Vector2> faceUVs = new List<Vector2>(64);

        const float EPSILON = 0.01f;


        public static void AddFaceForCulling(Face brushFace)
        {
            allFaces.Add(brushFace);
        }

        public static void ClearFaceCullingList()
        {
            allFaces.Clear();
            discardedFaces.Clear();
        }

        public static void DiscardFace(Face brushFace)
        {
            discardedFaces.Add(brushFace);
        }

        public static bool IsFaceCulledDiscard(Face brushFace)
        {
            return discardedFaces.Contains(brushFace);
        }

        public static FaceCullingJobGroup StartFaceCullingJobs()
        {
            return new FaceCullingJobGroup();
        }

        public class FaceCullingJobGroup
        {
            NativeArray<int> cullingOffsets;
            NativeArray<Vector4> cullingPlanes;
            NativeArray<Vector3> cullingVerts;
            NativeArray<bool> cullingResults;
            JobHandle jobHandle;

            public FaceCullingJobGroup()
            {
                var vertCount = 0;
                cullingOffsets = new NativeArray<int>(allFaces.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                cullingPlanes = new NativeArray<Vector4>(allFaces.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < allFaces.Count; i++)
                {
                    cullingOffsets[i] = vertCount;
                    vertCount += allFaces[i].Vertices.Count;
                    cullingPlanes[i] = new Vector4(allFaces[i].Plane.Normal.X, allFaces[i].Plane.Normal.Y, allFaces[i].Plane.Normal.Z, allFaces[i].Plane.D);
                }

                cullingVerts = new NativeArray<Vector3>(vertCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < allFaces.Count; i++)
                {
                    for (int v = cullingOffsets[i]; v < (i < cullingOffsets.Length - 1 ? cullingOffsets[i + 1] : vertCount); v++)
                    {
                        cullingVerts[v] = allFaces[i].Vertices[v - cullingOffsets[i]].ToUnity();
                    }
                }

                cullingResults = new NativeArray<bool>(allFaces.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < allFaces.Count; i++)
                {
                    cullingResults[i] = IsFaceCulledDiscard(allFaces[i]);
                }

                var jobData = new FaceCullingJob();

#if SCOPA_USE_BURST
                jobData.faceVertices = cullingVerts.Reinterpret<float3>();
                jobData.facePlanes = cullingPlanes.Reinterpret<float4>();
#else
                jobData.faceVertices = cullingVerts;
                jobData.facePlanes = cullingPlanes;
#endif

                jobData.faceVertexOffsets = cullingOffsets;
                jobData.cullFaceResults = cullingResults;
                jobHandle = jobData.Schedule(cullingResults.Length, 32);
            }

            public void Complete()
            {
                jobHandle.Complete();

                // int culledFaces = 0;
                for (int i = 0; i < cullingResults.Length; i++)
                {
                    // if (!allFaces[i].discardWhenBuildingMesh && cullingResults[i])
                    //     culledFaces++;
                    if (cullingResults[i])
                        discardedFaces.Add(allFaces[i]);
                }
                // Debug.Log($"Culled {culledFaces} faces!");

                cullingOffsets.Dispose();
                cullingVerts.Dispose();
                cullingPlanes.Dispose();
                cullingResults.Dispose();
            }

        }

#if SCOPA_USE_BURST
        [BurstCompile]
#endif
        public struct FaceCullingJob : IJobParallelFor
        {
            [ReadOnlyAttribute]
#if SCOPA_USE_BURST
            public NativeArray<float3> faceVertices;
#else
            public NativeArray<Vector3> faceVertices;
#endif

            [ReadOnlyAttribute]
#if SCOPA_USE_BURST
            public NativeArray<float4> facePlanes;
#else
            public NativeArray<Vector4> facePlanes;
#endif

            [ReadOnlyAttribute]
            public NativeArray<int> faceVertexOffsets;

            public NativeArray<bool> cullFaceResults;

            public void Execute(int i)
            {
                if (cullFaceResults[i])
                    return;

                // test against all other faces
                for (int n = 0; n < faceVertexOffsets.Length; n++)
                {
                    // first, test (1) share similar plane distance and (2) face opposite directions
                    // we are testing the NEGATIVE case for early out
#if SCOPA_USE_BURST
                    if ( math.abs(facePlanes[i].w + facePlanes[n].w) > 0.5f || math.dot(facePlanes[i].xyz, facePlanes[n].xyz) > -0.999f )
                        continue;
#else
                    if (Mathf.Abs(facePlanes[i].w + facePlanes[n].w) > 0.5f || Vector3.Dot(facePlanes[i], facePlanes[n]) > -0.999f)
                        continue;
#endif

                    // then, test whether this face's vertices are completely inside the other
                    var offsetStart = faceVertexOffsets[i];
                    var offsetEnd = i < faceVertexOffsets.Length - 1 ? faceVertexOffsets[i + 1] : faceVertices.Length;

                    var Center = faceVertices[offsetStart];
                    for (int b = offsetStart + 1; b < offsetEnd; b++)
                    {
                        Center += faceVertices[b];
                    }
                    Center /= offsetEnd - offsetStart;

                    // 2D math is easier, so let's ignore the least important axis
                    var ignoreAxis = GetMainAxisToNormal(facePlanes[i]);

                    var otherOffsetStart = faceVertexOffsets[n];
                    var otherOffsetEnd = n < faceVertexOffsets.Length - 1 ? faceVertexOffsets[n + 1] : faceVertices.Length;

#if SCOPA_USE_BURST
                    var polygon = new NativeArray<float3>(otherOffsetEnd-otherOffsetStart, Allocator.Temp);
                    NativeArray<float3>.Copy(faceVertices, otherOffsetStart, polygon, 0, polygon.Length);
#else
                    var polygon = new Vector3[otherOffsetEnd - otherOffsetStart];
                    NativeArray<Vector3>.Copy(faceVertices, otherOffsetStart, polygon, 0, polygon.Length);
#endif

                    var vertNotInOtherFace = false;
                    for (int x = offsetStart; x < offsetEnd; x++)
                    {
#if SCOPA_USE_BURST
                        var p = faceVertices[x] + math.normalize(Center - faceVertices[x]) * 0.2f;
#else
                        var p = faceVertices[x] + Vector3.Normalize(Center - faceVertices[x]) * 0.2f;
#endif
                        switch (ignoreAxis)
                        {
                            case Axis.X: if (!IsInPolygonYZ(p, polygon)) vertNotInOtherFace = true; break;
                            case Axis.Y: if (!IsInPolygonXZ(p, polygon)) vertNotInOtherFace = true; break;
                            case Axis.Z: if (!IsInPolygonXY(p, polygon)) vertNotInOtherFace = true; break;
                        }

                        if (vertNotInOtherFace)
                            break;
                    }

#if SCOPA_USE_BURST
                    polygon.Dispose();
#endif

                    if (vertNotInOtherFace)
                        continue;

                    // if we got this far, then this face should be culled
                    var tempResult = true;
                    cullFaceResults[i] = tempResult;
                    return;
                }

            }
        }

        public enum Axis { X, Y, Z }

#if SCOPA_USE_BURST
        public static Axis GetMainAxisToNormal(float4 vec) {
            // VHE prioritises the axes in order of X, Y, Z.
            // so in Unity land, that's X, Z, and Y
            var norm = new float3(
                math.abs(vec.x), 
                math.abs(vec.y),
                math.abs(vec.z)
            );

            if (norm.x >= norm.y && norm.x >= norm.z) return Axis.X;
            if (norm.z >= norm.y) return Axis.Z;
            return Axis.Y;
        }

        public static bool IsInPolygonXY(float3 p, NativeArray<float3> polygon ) {
            // https://wrf.ecse.rpi.edu/Research/Short_Notes/pnpoly.html
            bool inside = false;
            for ( int i = 0, j = polygon.Length - 1 ; i < polygon.Length ; j = i++ ) {
                if ( ( polygon[i].y > p.y ) != ( polygon[j].y > p.y ) &&
                    p.x < ( polygon[j].x - polygon[i].x ) * ( p.y - polygon[i].y ) / ( polygon[j].y - polygon[i].y ) + polygon[i].x )
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        public static bool IsInPolygonYZ(float3 p, NativeArray<float3> polygon ) {
            // https://wrf.ecse.rpi.edu/Research/Short_Notes/pnpoly.html
            bool inside = false;
            for ( int i = 0, j = polygon.Length - 1 ; i < polygon.Length ; j = i++ ) {
                if ( ( polygon[i].y > p.y ) != ( polygon[j].y > p.y ) &&
                    p.z < ( polygon[j].z - polygon[i].z ) * ( p.y - polygon[i].y ) / ( polygon[j].y - polygon[i].y ) + polygon[i].z )
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        public static bool IsInPolygonXZ(float3 p, NativeArray<float3> polygon ) {
            // https://wrf.ecse.rpi.edu/Research/Short_Notes/pnpoly.html
            bool inside = false;
            for ( int i = 0, j = polygon.Length - 1 ; i < polygon.Length ; j = i++ ) {
                if ( ( polygon[i].x > p.x ) != ( polygon[j].x > p.x ) &&
                    p.z < ( polygon[j].z - polygon[i].z ) * ( p.x - polygon[i].x ) / ( polygon[j].x - polygon[i].x ) + polygon[i].z )
                {
                    inside = !inside;
                }
            }

            return inside;
        }
#endif

        public static Axis GetMainAxisToNormal(Vector3 norm)
        {
            // VHE prioritises the axes in order of X, Y, Z.
            // so in Unity land, that's X, Z, and Y
            norm = norm.Absolute();

            if (norm.x >= norm.y && norm.x >= norm.z) return Axis.X;
            if (norm.z >= norm.y) return Axis.Z;
            return Axis.Y;
        }

        public static bool IsInPolygonXY(Vector3 p, Vector3[] polygon)
        {
            // https://wrf.ecse.rpi.edu/Research/Short_Notes/pnpoly.html
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                if ((polygon[i].y > p.y) != (polygon[j].y > p.y) &&
                    p.x < (polygon[j].x - polygon[i].x) * (p.y - polygon[i].y) / (polygon[j].y - polygon[i].y) + polygon[i].x)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        public static bool IsInPolygonYZ(Vector3 p, Vector3[] polygon)
        {
            // https://wrf.ecse.rpi.edu/Research/Short_Notes/pnpoly.html
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                if ((polygon[i].y > p.y) != (polygon[j].y > p.y) &&
                    p.z < (polygon[j].z - polygon[i].z) * (p.y - polygon[i].y) / (polygon[j].y - polygon[i].y) + polygon[i].z)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        public static bool IsInPolygonXZ(Vector3 p, Vector3[] polygon)
        {
            // https://wrf.ecse.rpi.edu/Research/Short_Notes/pnpoly.html
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                if ((polygon[i].x > p.x) != (polygon[j].x > p.x) &&
                    p.z < (polygon[j].z - polygon[i].z) * (p.x - polygon[i].x) / (polygon[j].x - polygon[i].x) + polygon[i].z)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        public class MeshBuildingJobGroup
        {
            // Fields
            private NativeArray<int> vertexOffsets, triIndexCounts; // index = i
            private NativeArray<Vector3> vertices;
            private NativeArray<Vector4> uAxis, vAxis; // index = i, .w = scale
            private NativeArray<float> rotation;
            private NativeArray<Vector2> shift, uvOverride; // index = i
            private int totalVertexCount, totalTriIndexCount;

            public Mesh.MeshDataArray outputMesh;
            private Mesh generatedMesh;
            private JobHandle jobHandle;
            private ScopaMapConfig config;

            // Constructor
            public MeshBuildingJobGroup(string meshName, Vector3 meshOrigin, Solid solid, ScopaMapConfig config, ScopaMapConfig.MaterialOverride materialOverride = null, bool includeDiscardedFaces = false)
            {
                this.config = config;
                var faceList = new List<Face>();

                foreach (var face in solid.Faces)
                {
                    if (!includeDiscardedFaces && IsFaceCulledDiscard(face)) continue;

                    if (materialOverride != null &&
                        materialOverride.textureName.ToLowerInvariant().GetHashCode() != face.TextureName.ToLowerInvariant().GetHashCode()) continue;

                    faceList.Add(face);
                }

                InitializeFaceArrays(faceList);
                InitializeMeshData(faceList, meshOrigin, materialOverride);
                InitializeJob(faceList, meshOrigin, materialOverride, config.scalingFactor, config.globalTexelScale);

                generatedMesh = new Mesh { name = meshName };
            }

            // Initialize native arrays for face data
            private void InitializeFaceArrays(List<Face> faceList)
            {
                vertexOffsets = new NativeArray<int>(faceList.Count + 1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                triIndexCounts = new NativeArray<int>(faceList.Count + 1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

                for (int i = 0; i < faceList.Count; i++)
                {
                    vertexOffsets[i] = totalVertexCount;
                    totalVertexCount += faceList[i].Vertices.Count;

                    triIndexCounts[i] = totalTriIndexCount;
                    totalTriIndexCount += (faceList[i].Vertices.Count - 2) * 3;
                }

                vertexOffsets[faceList.Count] = totalVertexCount;
                triIndexCounts[faceList.Count] = totalTriIndexCount;

                vertices = new NativeArray<Vector3>(totalVertexCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                uAxis = new NativeArray<Vector4>(faceList.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                vAxis = new NativeArray<Vector4>(faceList.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                rotation = new NativeArray<float>(faceList.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                shift = new NativeArray<Vector2>(faceList.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                uvOverride = new NativeArray<Vector2>(totalVertexCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            }

            // Fill mesh data and UV overrides
            private void InitializeMeshData(List<Face> faceList, Vector3 meshOrigin, ScopaMapConfig.MaterialOverride materialOverride)
            {
                for (int i = 0; i < faceList.Count; i++)
                {
                    bool uvOverrideApplied = ApplyMaterialOverride(faceList[i], materialOverride, i);

                    if (!uvOverrideApplied)
                    {
                        FillDefaultUVs(i);
                    }

                    FillFaceVertexData(faceList[i], i);
                }

                outputMesh = Mesh.AllocateWritableMeshData(1);
                var meshData = outputMesh[0];

                meshData.SetVertexBufferParams(totalVertexCount,
                    new VertexAttributeDescriptor(VertexAttribute.Position),
                    new VertexAttributeDescriptor(VertexAttribute.Normal, stream: 1),
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord0, dimension: 2, stream: 2));

                meshData.SetIndexBufferParams(totalTriIndexCount, IndexFormat.UInt32);
            }

            // Applies material override for UVs, returns true if applied, false if not
            private bool ApplyMaterialOverride(Face face, ScopaMapConfig.MaterialOverride materialOverride, int faceIndex)
            {
                if (materialOverride != null &&
                    materialOverride.materialConfig != null &&
                    materialOverride.materialConfig.useOnBuildBrushFace &&
                    materialOverride.materialConfig.OnBuildBrushFace(face, config, out var overrideUVs))
                {
                    for (int u = 0; u < overrideUVs.Length; u++)
                    {
                        uvOverride[vertexOffsets[faceIndex] + u] = overrideUVs[u];
                    }
                    return true;
                }

                return false;
            }

            // Fills the UVs with dummy values
            private void FillDefaultUVs(int faceIndex)
            {
                for (int u = vertexOffsets[faceIndex]; u < vertexOffsets[faceIndex + 1]; u++)
                {
                    uvOverride[u] = new Vector2(MeshBuildingJob.IGNORE_UV, MeshBuildingJob.IGNORE_UV);
                }
            }

            // Fills vertex and face data for each face
            private void FillFaceVertexData(Face face, int faceIndex)
            {
                for (int v = vertexOffsets[faceIndex]; v < vertexOffsets[faceIndex + 1]; v++)
                {
                    vertices[v] = face.Vertices[v - vertexOffsets[faceIndex]].ToUnity();
                }

                uAxis[faceIndex] = new Vector4(face.UAxis.X, face.UAxis.Y, face.UAxis.Z, face.XScale);
                vAxis[faceIndex] = new Vector4(face.VAxis.X, face.VAxis.Y, face.VAxis.Z, face.YScale);
                rotation[faceIndex] = face.Rotation;
                shift[faceIndex] = new Vector2(face.XShift, face.YShift);
            }

            // Initializes the job with appropriate data
            private void InitializeJob(List<Face> faceList, Vector3 meshOrigin, ScopaMapConfig.MaterialOverride materialOverride, float scalingFactor, float globalTexelScale)
            {
                var jobData = new MeshBuildingJob
                {
                    faceVertexOffsets = vertexOffsets,
                    faceTriIndexCounts = triIndexCounts,

#if SCOPA_USE_BURST
                    faceVertices = vertices.Reinterpret<float3>(),
                    faceU = uAxis.Reinterpret<float4>(),
                    faceV = vAxis.Reinterpret<float4>(),
                    faceRot = rotation.Reinterpret<float>(),
                    faceShift = shift.Reinterpret<float2>(),
                    uvOverride = uvOverride.Reinterpret<float2>(),
#else
                    faceVertices = vertices,
                    faceU = uAxis,
                    faceV = vAxis,
                    faceRot = rotation,
                    faceShift = shift,
                    uvOverride = uvOverride,
#endif
                    meshData = outputMesh[0],
                    scalingFactor = scalingFactor,
                    globalTexelScale = globalTexelScale,
                    textureWidth = materialOverride?.material?.mainTexture != null ? materialOverride.material.mainTexture.width : config.defaultTexSize,
                    textureHeight = materialOverride?.material?.mainTexture != null ? materialOverride.material.mainTexture.height : config.defaultTexSize,
                    meshOrigin = meshOrigin
                };

                jobHandle = jobData.Schedule(faceList.Count, 128);
            }


            // Completes the job and finalizes the mesh
            public Mesh Complete()
            {
                jobHandle.Complete();

                var meshData = outputMesh[0];
                meshData.subMeshCount = 1;
                meshData.SetSubMesh(0, new SubMeshDescriptor(0, totalTriIndexCount), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);

                Mesh.ApplyAndDisposeWritableMeshData(outputMesh, generatedMesh);
                generatedMesh.RecalculateNormals();
                generatedMesh.RecalculateBounds();

                if (config.addTangents)
                {
                    generatedMesh.RecalculateTangents();
                }

#if UNITY_EDITOR
                if (config.addLightmapUV2)
                {
                    try
                    {
                        UnwrapParam.SetDefaults(out var unwrap);
                        unwrap.packMargin *= 2;
                        Unwrapping.GenerateSecondaryUVSet(generatedMesh, unwrap);
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                if (config.meshCompression != ScopaMapConfig.ModelImporterMeshCompression.Off)
                    UnityEditor.MeshUtility.SetMeshCompression(generatedMesh, (ModelImporterMeshCompression)config.meshCompression);
#endif

                DisposeNativeArrays();
                return generatedMesh;
            }

            // Dispose of native arrays
            private void DisposeNativeArrays()
            {

                vertexOffsets.Dispose();
                triIndexCounts.Dispose();
                vertices.Dispose();
                uvOverride.Dispose();
                uAxis.Dispose();
                vAxis.Dispose();
                rotation.Dispose();
                shift.Dispose();
            }
        }


#if SCOPA_USE_BURST
        [BurstCompile]
#endif
        public struct MeshBuildingJob : IJobParallelFor
        {
            [ReadOnlyAttribute] public NativeArray<int> faceVertexOffsets, faceTriIndexCounts; // index = i

#if SCOPA_USE_BURST
            [ReadOnlyAttribute] public NativeArray<float3> faceVertices;
            [ReadOnlyAttribute] public NativeArray<float4> faceU, faceV; // index = i, .w = scale
            [ReadOnlyAttribute] public NativeArray<float> faceRot;
            [ReadOnlyAttribute] public NativeArray<float2> faceShift, uvOverride; // index = i
            [ReadOnlyAttribute] public float3 meshOrigin;
#else
            [ReadOnlyAttribute] public NativeArray<Vector3> faceVertices;
            [ReadOnlyAttribute] public NativeArray<Vector4> faceU, faceV; // index = i, .w = scale
            [ReadOnlyAttribute] public NativeArray<float> faceRot;
            [ReadOnlyAttribute] public NativeArray<Vector2> faceShift, uvOverride; // index = i
            [ReadOnlyAttribute] public Vector3 meshOrigin;
#endif

            [NativeDisableParallelForRestriction]
            public Mesh.MeshData meshData;

            [ReadOnlyAttribute] public float scalingFactor, globalTexelScale, textureWidth, textureHeight;
            public const float IGNORE_UV = -999999999f;

            public void Execute(int i)
            {
                var offsetStart = faceVertexOffsets[i];
                var offsetEnd = faceVertexOffsets[i + 1];

#if SCOPA_USE_BURST
                var outputVerts = meshData.GetVertexData<float3>();
                var outputUVs = meshData.GetVertexData<float2>(2);
#else
                var outputVerts = meshData.GetVertexData<Vector3>();
                var outputUVs = meshData.GetVertexData<Vector2>(2);
#endif

                var outputTris = meshData.GetIndexData<int>();

                if (textureWidth > 2048)
                {
                    Debug.LogWarning("Texture width for a brush is greater than 2048! This may cause importing issues...");
                }

                if (textureHeight > 2048)
                {
                    Debug.LogWarning("Texture height for a brush is greater than 2048! This may cause importing issues...");
                }

                // add all verts, normals, and UVs
                for (int n = offsetStart; n < offsetEnd; n++)
                {
                    outputVerts[n] = faceVertices[n] * scalingFactor - meshOrigin;

                    if (uvOverride[n].x > IGNORE_UV)
                    {
                        outputUVs[n] = uvOverride[n];
                    }
                    else
                    {
#if SCOPA_USE_BURST
                        //var rotationRad = -math.radians(faceRot[i]);
                        outputUVs[n] = new float2(
                            (math.dot(faceVertices[n], faceU[i].xyz / faceU[i].w) + (faceShift[i].x % textureWidth)) / textureWidth,
                            (math.dot(faceVertices[n], faceV[i].xyz / -faceV[i].w) + (-faceShift[i].y % textureHeight)) / textureHeight
                        );

                        RotateUVs(outputUVs, faceRot[i]);

                        //var rotatedVector = new float2
                        //{
                        //    x = (outputUVs[n].x * math.cos(rotationRad)) - (outputUVs[n].y * math.sin(rotationRad)),
                        //    y = (outputUVs[n].x * math.sin(rotationRad)) + (outputUVs[n].y * math.cos(rotationRad))
                        //};
                        //outputUVs[n] = rotatedVector * globalTexelScale;

                        //var faceUScaled = math.dot(faceVertices[n], faceU[i].xyz / faceU[i].w);
                        //var faceVScaled = math.dot(faceVertices[n], faceV[i].xyz / -faceV[i].w);

                        //// Rotation part
                        //var rotationRad = -math.radians(faceRot[i]);
                        //var rotatedVector = new float2
                        //{
                        //    x = (faceUScaled * math.cos(rotationRad)) - (faceVScaled * math.sin(rotationRad)),
                        //    y = (faceUScaled * math.sin(rotationRad)) + (faceVScaled * math.cos(rotationRad))
                        //};

                        //// UV shifting
                        //outputUVs[n] = new float2(
                        //    (faceShift[i].x % textureWidth) / textureWidth,
                        //    (-faceShift[i].y % textureHeight) / textureHeight
                        //);

                        //// Apply global texel scale after rotation
                        //outputUVs[n] = rotatedVector * globalTexelScale;
#else

                        // Step 1: Calculate the base UVs without rotation
                        outputUVs[n] = new Vector2(
                            (Vector3.Dot(faceVertices[n], faceU[i])),
                            (Vector3.Dot(faceVertices[n], faceV[i]))
                        );

                        // Step 2: Rotate the UVs around the origin (0, 0)
                        outputUVs[n] = RotateUVs(outputUVs[n], faceRot[i]);


                        // Step 3: Apply base scaling BEFORE translation
                        outputUVs[n] = new Vector2(
                            outputUVs[n].x / (faceU[i].w),
                            outputUVs[n].y / (-faceV[i].w)
                        );



                        // Step 5: Apply translation related to faceShift BEFORE scaling to unity units
                        outputUVs[n] += new Vector2(faceShift[i].x, -faceShift[i].y);

                        outputUVs[n] = new Vector2(
                            outputUVs[n].x / textureWidth,
                            outputUVs[n].y / textureHeight
                        );

                        // Step 6: Optionally apply global texel scale if needed
                        outputUVs[n] *= globalTexelScale;
#endif
                    }
                }

                // verts are already in correct order, add as basic fan pattern (since we know it's a convex face)
                for (int t = 2; t < offsetEnd - offsetStart; t++)
                {
                    outputTris[faceTriIndexCounts[i] + (t - 2) * 3] = offsetStart;
                    outputTris[faceTriIndexCounts[i] + (t - 2) * 3 + 1] = offsetStart + t - 1;
                    outputTris[faceTriIndexCounts[i] + (t - 2) * 3 + 2] = offsetStart + t;
                }
            }
        }
#if SCOPA_USE_BURST
        static void RotateUVs(float2[] uvs, float angle = 90)
        {
            var center = (SmallestVector2(uvs) + LargestVector2(uvs)) / 2;
            for (int i = 0; i < uvs.Length; i++)
            {
                uvs[i] = quaternion.Euler(0, 0, angle) * (uvs[i] - center) + (float3)center;
            }
        }

        static float2 SmallestVector2(float2[] v)
        {
            int len = v.Length;
            float2 l = v[0];
            for (int i = 0; i < len; i++)
            {
                if (v[i].x < l.x) l.x = v[i].x;
                if (v[i].y < l.y) l.y = v[i].y;
            }
            return l;
        }

        static float2 LargestVector2(float2[] v)
        {
            int len = v.Length;
            float2 l = v[0];
            for (int i = 0; i < len; i++)
            {
                if (v[i].x > l.x) l.x = v[i].x;
                if (v[i].y > l.y) l.y = v[i].y;
            }
            return l;
        }



#else
        static Vector2 RotateUVs(Vector2 uv, float angle = 90)
        {
            //var center = (SmallestVector2(uvs) + LargestVector2(uvs)) / 2;
            return Quaternion.Euler(0, 0, angle) * (uv);// - center) + (Vector3)center;
        }

        static Vector2 SmallestVector2(NativeArray<Vector2> v)
        {
            int len = v.Length;
            Vector2 l = v[0];
            for (int i = 0; i < len; i++)
            {
                if (v[i].x < l.x)
                    l.x = v[i].x;

                if (v[i].y < l.y)
                    l.y = v[i].y;
            }
            return l;
        }

        static Vector2 LargestVector2(NativeArray<Vector2> v)
        {
            int len = v.Length;
            Vector2 l = v[0];
            for (int i = 0; i < len; i++)
            {
                if (v[i].x > l.x)
                    l.x = v[i].x;

                if (v[i].y > l.y)
                    l.y = v[i].y;
            }
            return l;
        }
#endif
        public class ColliderJobGroup
        {

            NativeArray<int> faceVertexOffsets, faceTriIndexCounts, solidFaceOffsets; // index = i
            NativeArray<Vector3> faceVertices, facePlaneNormals;
            NativeArray<bool> canBeBoxCollider;
            int vertCount, triIndexCount, faceCount;

            public GameObject gameObject;
            public Mesh.MeshDataArray outputMesh;
            JobHandle jobHandle;
            Mesh[] meshes;
            bool isTrigger, isConvex;

            public ColliderJobGroup(GameObject gameObject, bool isTrigger, bool forceConvex, string colliderNameFormat, IEnumerable<Solid> solids, ScopaMapConfig config, Dictionary<Solid, Entity> mergedEntityData)
            {
                this.gameObject = gameObject;

                var faceList = new List<Face>();
                var solidFaceOffsetsManaged = new List<int>();
                var solidCount = 0;
                this.isTrigger = isTrigger;
                this.isConvex = forceConvex || config.colliderMode != ScopaMapConfig.ColliderImportMode.MergeAllToOneConcaveMeshCollider;
                foreach (var solid in solids)
                {
                    if (mergedEntityData.ContainsKey(solid) && (config.IsEntityNonsolid(mergedEntityData[solid].ClassName) || config.IsEntityTrigger(mergedEntityData[solid].ClassName)))
                        continue;

                    foreach (var face in solid.Faces)
                    {
                        faceList.Add(face);
                    }

                    // if forceConvex or MergeAllToOneConcaveMeshCollider, then pretend it's all just one giant brush
                    // unless it's a trigger, then it MUST be convex
                    if (isTrigger || (!forceConvex && config.colliderMode != ScopaMapConfig.ColliderImportMode.MergeAllToOneConcaveMeshCollider) || solidCount == 0)
                    {
                        solidFaceOffsetsManaged.Add(faceCount);
                        solidCount++;
                    }

                    faceCount += solid.Faces.Count;
                }
                solidFaceOffsetsManaged.Add(faceCount);

                solidFaceOffsets = new NativeArray<int>(solidFaceOffsetsManaged.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                solidFaceOffsets.CopyFrom(solidFaceOffsetsManaged.ToArray());
                canBeBoxCollider = new NativeArray<bool>(solidCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

                faceVertexOffsets = new NativeArray<int>(faceList.Count + 1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                faceTriIndexCounts = new NativeArray<int>(faceList.Count + 1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < faceList.Count; i++)
                {
                    faceVertexOffsets[i] = vertCount;
                    vertCount += faceList[i].Vertices.Count;
                    faceTriIndexCounts[i] = triIndexCount;
                    triIndexCount += (faceList[i].Vertices.Count - 2) * 3;
                }
                faceVertexOffsets[faceVertexOffsets.Length - 1] = vertCount;
                faceTriIndexCounts[faceTriIndexCounts.Length - 1] = triIndexCount;

                faceVertices = new NativeArray<Vector3>(vertCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                facePlaneNormals = new NativeArray<Vector3>(faceList.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < faceList.Count; i++)
                {
                    for (int v = faceVertexOffsets[i]; v < faceVertexOffsets[i + 1]; v++)
                    {
                        faceVertices[v] = faceList[i].Vertices[v - faceVertexOffsets[i]].ToUnity();
                    }
                    facePlaneNormals[i] = faceList[i].Plane.Normal.ToUnity();
                }

                outputMesh = Mesh.AllocateWritableMeshData(solidCount);
                meshes = new Mesh[solidCount];
                for (int i = 0; i < solidCount; i++)
                {
                    meshes[i] = new Mesh();
                    meshes[i].name = string.Format(colliderNameFormat, i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture));

                    var solidOffsetStart = solidFaceOffsets[i];
                    var solidOffsetEnd = solidFaceOffsets[i + 1];
                    var finalVertCount = faceVertexOffsets[solidOffsetEnd] - faceVertexOffsets[solidOffsetStart];
                    var finalTriCount = faceTriIndexCounts[solidOffsetEnd] - faceTriIndexCounts[solidOffsetStart];

                    var meshData = outputMesh[i];
                    meshData.SetVertexBufferParams(finalVertCount,
                        new VertexAttributeDescriptor(VertexAttribute.Position)
                    );
                    meshData.SetIndexBufferParams(finalTriCount, IndexFormat.UInt32);
                }

                var jobData = new ColliderJob();
                jobData.faceVertexOffsets = faceVertexOffsets;
                jobData.faceTriIndexCounts = faceTriIndexCounts;
                jobData.solidFaceOffsets = solidFaceOffsets;

#if SCOPA_USE_BURST
                jobData.faceVertices = faceVertices.Reinterpret<float3>();
                jobData.facePlaneNormals = facePlaneNormals.Reinterpret<float3>();
#else
                jobData.faceVertices = faceVertices;
                jobData.facePlaneNormals = facePlaneNormals;
#endif

                jobData.meshDataArray = outputMesh;
                jobData.canBeBoxColliderResults = canBeBoxCollider;
                jobData.colliderMode = config.colliderMode;
                jobData.scalingFactor = config.scalingFactor;
                jobData.meshOrigin = gameObject.transform.position;
                jobHandle = jobData.Schedule(solidCount, 64);
            }

            public Mesh[] Complete()
            {
                jobHandle.Complete();

                Mesh.ApplyAndDisposeWritableMeshData(outputMesh, meshes);
                for (int i = 0; i < meshes.Length; i++)
                {
                    Mesh newMesh = meshes[i];
                    var newGO = new GameObject(newMesh.name);
                    newGO.transform.SetParent(gameObject.transform);
                    newGO.transform.localPosition = Vector3.zero;
                    newGO.transform.localRotation = Quaternion.identity;
                    newGO.transform.localScale = Vector3.one;

                    newMesh.RecalculateBounds();
                    if (canBeBoxCollider[i])
                    { // if box collider, we'll just use the mesh bounds to config a collider
                        var bounds = newMesh.bounds;
                        var boxCol = newGO.AddComponent<BoxCollider>();
                        boxCol.center = bounds.center;
                        boxCol.size = bounds.size;
                        boxCol.isTrigger = isTrigger;
                    }
                    else
                    { // but usually this is a convex mesh collider
                        var newMeshCollider = newGO.AddComponent<MeshCollider>();
                        newMeshCollider.convex = isTrigger ? true : isConvex;
                        newMeshCollider.isTrigger = isTrigger;
                        newMeshCollider.sharedMesh = newMesh;
                    }
                }

                faceVertexOffsets.Dispose();
                faceTriIndexCounts.Dispose();
                solidFaceOffsets.Dispose();

                faceVertices.Dispose();
                facePlaneNormals.Dispose();
                canBeBoxCollider.Dispose();

                return meshes;
            }

#if SCOPA_USE_BURST
            [BurstCompile]
#endif
            public struct ColliderJob : IJobParallelFor
            {
                [ReadOnlyAttribute] public NativeArray<int> faceVertexOffsets, faceTriIndexCounts, solidFaceOffsets; // index = i

#if SCOPA_USE_BURST
                [ReadOnlyAttribute] public NativeArray<float3> faceVertices, facePlaneNormals;
                [ReadOnlyAttribute] public float3 meshOrigin;
#else
                [ReadOnlyAttribute] public NativeArray<Vector3> faceVertices, facePlaneNormals;
                [ReadOnlyAttribute] public Vector3 meshOrigin;
#endif

                public Mesh.MeshDataArray meshDataArray;
                [WriteOnly] public NativeArray<bool> canBeBoxColliderResults;

                [ReadOnlyAttribute] public float scalingFactor;
                [ReadOnlyAttribute] public ScopaMapConfig.ColliderImportMode colliderMode;

                public void Execute(int i)
                {
                    var solidOffsetStart = solidFaceOffsets[i];
                    var solidOffsetEnd = solidFaceOffsets[i + 1];

                    var solidVertStart = faceVertexOffsets[solidOffsetStart];
                    var finalTriIndexCount = faceTriIndexCounts[solidOffsetEnd] - faceTriIndexCounts[solidOffsetStart];

                    var meshData = meshDataArray[i];

#if SCOPA_USE_BURST
                    var outputVerts = meshData.GetVertexData<float3>();
#else
                    var outputVerts = meshData.GetVertexData<Vector3>();
#endif

                    var outputTris = meshData.GetIndexData<int>();

                    // for each solid, gather faces...
                    var canBeBoxCollider = colliderMode == ScopaMapConfig.ColliderImportMode.BoxAndConvex || colliderMode == ScopaMapConfig.ColliderImportMode.BoxColliderOnly;
                    for (int face = solidOffsetStart; face < solidOffsetEnd; face++)
                    {
                        // don't bother doing BoxCollider test if we're forcing BoxColliderOnly
                        if (canBeBoxCollider && colliderMode != ScopaMapConfig.ColliderImportMode.BoxColliderOnly)
                        {
                            // but otherwise, test if all face normals are axis aligned... if so, it can be a box collider
#if SCOPA_USE_BURST
                            var absNormal = math.abs(facePlaneNormals[face]);
#else
                            var absNormal = facePlaneNormals[face].Absolute();
#endif

                            canBeBoxCollider = !((absNormal.x > 0.01f && absNormal.x < 0.99f)
                                            || (absNormal.z > 0.01f && absNormal.z < 0.99f)
                                            || (absNormal.y > 0.01f && absNormal.y < 0.99f));
                        }

                        var vertOffsetStart = faceVertexOffsets[face];
                        var vertOffsetEnd = faceVertexOffsets[face + 1];
                        for (int n = vertOffsetStart; n < vertOffsetEnd; n++)
                        {
                            outputVerts[n - solidVertStart] = faceVertices[n] * scalingFactor - meshOrigin;
                        }

                        // verts are already in correct order, add as basic fan pattern (since we know it's a convex face)
                        var triIndexStart = faceTriIndexCounts[face] - faceTriIndexCounts[solidOffsetStart];
                        var faceVertStart = vertOffsetStart - solidVertStart;
                        for (int t = 2; t < vertOffsetEnd - vertOffsetStart; t++)
                        {
                            outputTris[triIndexStart + (t - 2) * 3] = faceVertStart;
                            outputTris[triIndexStart + (t - 2) * 3 + 1] = faceVertStart + t - 1;
                            outputTris[triIndexStart + (t - 2) * 3 + 2] = faceVertStart + t;
                        }
                    }

                    canBeBoxColliderResults[i] = canBeBoxCollider;
                    meshData.subMeshCount = 1;
                    meshData.SetSubMesh(
                        0,
                        new SubMeshDescriptor(0, finalTriIndexCount)
                    );
                }
            }
        }

        public static void WeldVertices(this Mesh aMesh, float aMaxDelta = 0.1f, float maxAngle = 180f)
        {
            var verts = aMesh.vertices;
            var normals = aMesh.normals;
            var uvs = aMesh.uv;
            List<int> newVerts = new List<int>();
            int[] map = new int[verts.Length];
            // create mapping and filter duplicates.
            for (int i = 0; i < verts.Length; i++)
            {
                var p = verts[i];
                var n = normals[i];
                var uv = uvs[i];
                bool duplicate = false;
                for (int i2 = 0; i2 < newVerts.Count; i2++)
                {
                    int a = newVerts[i2];
                    if (
                        (verts[a] - p).sqrMagnitude <= aMaxDelta // compare position
                        && Vector3.Angle(normals[a], n) <= maxAngle // compare normal
                                                                    // && (uvs[a] - uv).sqrMagnitude <= aMaxDelta // compare first uv coordinate
                        )
                    {
                        map[i] = i2;
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                {
                    map[i] = newVerts.Count;
                    newVerts.Add(i);
                }
            }
            // create new vertices
            var verts2 = new Vector3[newVerts.Count];
            var normals2 = new Vector3[newVerts.Count];
            var uvs2 = new Vector2[newVerts.Count];
            for (int i = 0; i < newVerts.Count; i++)
            {
                int a = newVerts[i];
                verts2[i] = verts[a];
                normals2[i] = normals[a];
                uvs2[i] = uvs[a];
            }
            // map the triangle to the new vertices
            var tris = aMesh.triangles;
            for (int i = 0; i < tris.Length; i++)
            {
                tris[i] = map[tris[i]];
            }
            aMesh.Clear();
            aMesh.vertices = verts2;
            aMesh.normals = normals2;
            aMesh.triangles = tris;
            aMesh.uv = uvs2;
        }

        public static void SmoothNormalsJobs(this Mesh aMesh, float weldingAngle = 80, float maxDelta = 0.1f)
        {
            var meshData = Mesh.AcquireReadOnlyMeshData(aMesh);
            var verts = new NativeArray<Vector3>(meshData[0].vertexCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            meshData[0].GetVertices(verts);
            var normals = new NativeArray<Vector3>(meshData[0].vertexCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            meshData[0].GetNormals(normals);
            var smoothNormalsResults = new NativeArray<Vector3>(meshData[0].vertexCount, Allocator.TempJob);

            var jobData = new SmoothJob();
            jobData.cos = Mathf.Cos(weldingAngle * Mathf.Deg2Rad);
            jobData.maxDelta = maxDelta;

#if SCOPA_USE_BURST
            jobData.verts = verts.Reinterpret<float3>();
            jobData.normals = normals.Reinterpret<float3>();
            jobData.results = smoothNormalsResults.Reinterpret<float3>();
#else
            jobData.verts = verts;
            jobData.normals = normals;
            jobData.results = smoothNormalsResults;
#endif

            var handle = jobData.Schedule(smoothNormalsResults.Length, 8);
            handle.Complete();

            meshData.Dispose(); // must dispose this early, before modifying mesh

            aMesh.SetNormals(smoothNormalsResults);

            verts.Dispose();
            normals.Dispose();
            smoothNormalsResults.Dispose();
        }

#if SCOPA_USE_BURST
        [BurstCompile]
#endif
        public struct SmoothJob : IJobParallelFor
        {
#if SCOPA_USE_BURST
            [ReadOnlyAttribute] public NativeArray<float3> verts, normals;
            public NativeArray<float3> results;
#else
            [ReadOnlyAttribute] public NativeArray<Vector3> verts, normals;
            public NativeArray<Vector3> results;
#endif

            public float cos, maxDelta;

            public void Execute(int i)
            {
                var tempResult = normals[i];
                var resultCount = 1;

                for (int i2 = 0; i2 < verts.Length; i2++)
                {
#if SCOPA_USE_BURST
                    if ( math.lengthsq(verts[i2] - verts[i] ) <= maxDelta && math.dot(normals[i2], normals[i] ) >= cos ) 
#else
                    if ((verts[i2] - verts[i]).sqrMagnitude <= maxDelta && Vector3.Dot(normals[i2], normals[i]) >= cos)
#endif
                    {
                        tempResult += normals[i2];
                        resultCount++;
                    }
                }

                if (resultCount > 1)
#if SCOPA_USE_BURST
                    tempResult = math.normalize(tempResult / resultCount);
#else
                    tempResult = (tempResult / resultCount).normalized;
#endif
                results[i] = tempResult;
            }
        }

        public static void SnapBrushVertices(Solid sledgeSolid, float snappingDistance = 4f)
        {
            // snap nearby vertices together within in each solid -- but always snap to the FURTHEST vertex from the center
            var origin = new System.Numerics.Vector3();
            var vertexCount = 0;
            foreach (var face in sledgeSolid.Faces)
            {
                for (int i = 0; i < face.Vertices.Count; i++)
                {
                    origin += face.Vertices[i];
                }
                vertexCount += face.Vertices.Count;
            }
            origin /= vertexCount;

            foreach (var face1 in sledgeSolid.Faces)
            {
                foreach (var face2 in sledgeSolid.Faces)
                {
                    if (face1 == face2)
                        continue;

                    for (int a = 0; a < face1.Vertices.Count; a++)
                    {
                        for (int b = 0; b < face2.Vertices.Count; b++)
                        {
                            if ((face1.Vertices[a] - face2.Vertices[b]).LengthSquared() < snappingDistance * snappingDistance)
                            {
                                if ((face1.Vertices[a] - origin).LengthSquared() > (face2.Vertices[b] - origin).LengthSquared())
                                    face2.Vertices[b] = face1.Vertices[a];
                                else
                                    face1.Vertices[a] = face2.Vertices[b];
                            }
                        }
                    }
                }
            }
        }

    }
}