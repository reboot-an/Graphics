using System.Collections.Generic;
using UnityEngine.Rendering;
using System.Linq;
using System;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>
    /// Unity Monobehavior that manages the execution of custom passes.
    /// It provides 
    /// </summary>
    [ExecuteAlways]
    [HelpURL(Documentation.baseURL + Documentation.version + Documentation.subURL + "Custom-Pass" + Documentation.endURL)]
    public class CustomPassVolume : MonoBehaviour
    {
        /// <summary>
        /// Whether or not the volume is global. If true, the component will ignore all colliders attached to it
        /// </summary>
        public bool isGlobal = true;

        /// <summary>
        /// Distance where the volume start to be rendered, the fadeValue field in C# will be updated to the normalized blend factor for your custom C# passes
        /// In the fullscreen shader pass and DrawRenderers shaders you can access the _FadeValue
        /// </summary>
        [Min(0)]
        public float fadeRadius;

        /// <summary>
        /// List of custom passes to execute
        /// </summary>
        /// <typeparam name="CustomPass"></typeparam>
        /// <returns></returns>
        [SerializeReference]
        public List<CustomPass> customPasses = new List<CustomPass>();

        /// <summary>
        /// Where the custom passes are going to be injected in HDRP
        /// </summary>
        public CustomPassInjectionPoint injectionPoint = CustomPassInjectionPoint.BeforeTransparent;

        /// <summary>
        /// Fade value between 0 and 1. it represent how close you camera is from the collider of the custom pass.  
        /// 0 when the camera is outside the volume + fade radius and 1 when it is inside the collider.
        /// </summary>
        /// <value>The fade value that should be applied to the custom pass effect</value>
        public float fadeValue { get; private set; }

        // The current active custom pass volume is simply the smallest overlapping volume with the trigger transform
        static HashSet<CustomPassVolume>    m_ActivePassVolumes = new HashSet<CustomPassVolume>();
        static List<CustomPassVolume>       m_OverlappingPassVolumes = new List<CustomPassVolume>();

        List<Collider>          m_Colliders = new List<Collider>();
        List<Collider>          m_OverlappingColliders = new List<Collider>();

        static List<CustomPassInjectionPoint> m_InjectionPoints;
        static List<CustomPassInjectionPoint> injectionPoints
        {
            get
            {
                if (m_InjectionPoints == null)
                    m_InjectionPoints = Enum.GetValues(typeof(CustomPassInjectionPoint)).Cast<CustomPassInjectionPoint>().ToList();
                return m_InjectionPoints;
            }
        }

        void OnEnable()
        {
            // Remove null passes in case of something happens during the deserialization of the passes
            customPasses.RemoveAll(c => c is null);
            GetComponents(m_Colliders);
            Register(this);
        }

        void OnDisable() => UnRegister(this);

        void OnDestroy() => Cleanup();

        internal bool Execute(ScriptableRenderContext renderContext, CommandBuffer cmd, HDCameraInfo hdCamera, CullingResults cullingResult, SharedRTManager rtManager, CustomPass.RenderTargets targets)
        {
            bool executed = false;

            // We never execute volume if the layer is not within the culling layers of the camera
            if ((hdCamera.volumeLayerMask & (1 << gameObject.layer)) == 0)
                return false;

            Shader.SetGlobalFloat(HDShaderIDs._CustomPassInjectionPoint, (float)injectionPoint);

            foreach (var pass in customPasses)
            {
                if (pass != null && pass.enabled)
                    using (new ProfilingSample(cmd, pass.name))
                    {
                        pass.ExecuteInternal(renderContext, cmd, hdCamera, cullingResult, rtManager, targets, this);
                        executed = true;
                    }
            }

            return executed;
        }

        internal void CleanupPasses()
        {
            foreach (var pass in customPasses)
                pass.CleanupPassInternal();
        }

        static void Register(CustomPassVolume volume) => m_ActivePassVolumes.Add(volume);

        static void UnRegister(CustomPassVolume volume) => m_ActivePassVolumes.Remove(volume);

        internal static void Update(HDCameraInfo camera)
        {
            var triggerPos = camera.volumeAnchor.position;

            m_OverlappingPassVolumes.Clear();

            // Traverse all volumes
            foreach (var volume in m_ActivePassVolumes)
            {
                // Ignore volumes that are not in the camera layer mask
                if ((camera.volumeLayerMask & (1 << volume.gameObject.layer)) == 0)
                    continue;

                // Global volumes always have influence
                if (volume.isGlobal)
                {
                    volume.fadeValue = 1.0f;
                    m_OverlappingPassVolumes.Add(volume);
                    continue;
                }

                // If volume isn't global and has no collider, skip it as it's useless
                if (volume.m_Colliders.Count == 0)
                    continue;

                volume.m_OverlappingColliders.Clear();

                float sqrFadeRadius = Mathf.Max(float.Epsilon, volume.fadeRadius * volume.fadeRadius);
                float minSqrDistance = 1e20f;

                foreach (var collider in volume.m_Colliders)
                {
                    if (!collider || !collider.enabled)
                        continue;
                    
                    // We don't support concave colliders
                    if (collider is MeshCollider m && !m.convex)
                        continue;

                    var closestPoint = collider.ClosestPoint(triggerPos);
                    var d = (closestPoint - triggerPos).sqrMagnitude;
                    
                    minSqrDistance = Mathf.Min(minSqrDistance, d);

                    // Update the list of overlapping colliders
                    if (d <= sqrFadeRadius)
                        volume.m_OverlappingColliders.Add(collider);
                }
                
                // update the fade value:
                volume.fadeValue = 1.0f - Mathf.Clamp01(Mathf.Sqrt(minSqrDistance / sqrFadeRadius));

                if (volume.m_OverlappingColliders.Count > 0)
                    m_OverlappingPassVolumes.Add(volume);
            }

            // Sort the overlapping volumes by priority order (smaller first, then larger and finally globals)
            m_OverlappingPassVolumes.Sort((v1, v2) => {
                float GetVolumeExtent(CustomPassVolume volume)
                {
                    float extent = 0;
                    foreach (var collider in volume.m_OverlappingColliders)
                        extent += collider.bounds.extents.magnitude;
                    return extent;
                }

                if (v1.isGlobal && v2.isGlobal) return 0;
                if (v1.isGlobal) return 1;
                if (v2.isGlobal) return -1;
                
                return GetVolumeExtent(v1).CompareTo(GetVolumeExtent(v2));
            });
        }

        internal void AggregateCullingParameters(ref ScriptableCullingParameters cullingParameters, HDCameraInfo hdCamera)
        {
            foreach (var pass in customPasses)
            {
                if (pass != null && pass.enabled)
                    pass.InternalAggregateCullingParameters(ref cullingParameters, hdCamera);
            }
        }

        internal static CullingResults? Cull(ScriptableRenderContext renderContext, HDCameraInfo hdCamera)
        {
            CullingResults?  result = null;

            // We need to sort the volumes first to know which one will be executed
            // TODO: cache the results per camera in the HDRenderPipeline so it's not executed twice per camera
            Update(hdCamera);

            // For each injection points, we gather the culling results for 
            hdCamera.camera.TryGetCullingParameters(out var cullingParameters);

            // By default we don't want the culling to return any objects
            cullingParameters.cullingMask = 0;
            cullingParameters.cullingOptions &= CullingOptions.Stereo; // We just keep stereo if enabled and clear the other flags

            foreach (var injectionPoint in injectionPoints)
                GetActivePassVolume(injectionPoint)?.AggregateCullingParameters(ref cullingParameters, hdCamera);

            // If we don't have anything to cull or the pass is asking for the same culling layers than the camera, we don't have to re-do the culling
            if (cullingParameters.cullingMask != 0 && (cullingParameters.cullingMask & hdCamera.camera.cullingMask) != cullingParameters.cullingMask)
                result = renderContext.Cull(ref cullingParameters);

            return result;
        }

        internal static void Cleanup()
        {
            foreach (var pass in m_ActivePassVolumes)
            {
                pass.CleanupPasses();
            }
        }
        
        public static CustomPassVolume GetActivePassVolume(CustomPassInjectionPoint injectionPoint)
        {
            foreach (var volume in m_OverlappingPassVolumes)
                if (volume.injectionPoint == injectionPoint)
                    return volume;
            return null;
        }

        /// <summary>
        /// Add a pass of type passType in the active pass list
        /// </summary>
        /// <param name="passType"></param>
        public void AddPassOfType(Type passType)
        {
            if (!typeof(CustomPass).IsAssignableFrom(passType))
            {
                Debug.LogError($"Can't add pass type {passType} to the list because it does not inherit from CustomPass.");
                return ;
            }

            customPasses.Add(Activator.CreateInstance(passType) as CustomPass);
        }

#if UNITY_EDITOR
        // In the editor, we refresh the list of colliders at every frame because it's frequent to add/remove them
        void Update() => GetComponents(m_Colliders);

        void OnDrawGizmos()
        {
            if (isGlobal || m_Colliders.Count == 0 || !enabled)
                return;

            var scale = transform.localScale;
            var invScale = new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, scale);
            Gizmos.color = CoreRenderPipelinePreferences.volumeGizmoColor;

            // Draw a separate gizmo for each collider
            foreach (var collider in m_Colliders)
            {
                if (!collider || !collider.enabled)
                    continue;

                // We'll just use scaling as an approximation for volume skin. It's far from being
                // correct (and is completely wrong in some cases). Ultimately we'd use a distance
                // field or at least a tesselate + push modifier on the collider's mesh to get a
                // better approximation, but the current Gizmo system is a bit limited and because
                // everything is dynamic in Unity and can be changed at anytime, it's hard to keep
                // track of changes in an elegant way (which we'd need to implement a nice cache
                // system for generated volume meshes).
                switch (collider)
                {
                    case BoxCollider c:
                        Gizmos.DrawCube(c.center, c.size);
                        if (fadeRadius > 0)
                        {
                            // invert te scale for the fade radius because it's in fixed units
                            Vector3 s = new Vector3(
                                (fadeRadius * 2) / scale.x,
                                (fadeRadius * 2) / scale.y,
                                (fadeRadius * 2) / scale.z
                            );
                            Gizmos.DrawWireCube(c.center, c.size + s);
                        }
                        break;
                    case SphereCollider c:
                        // For sphere the only scale that is used is the transform.x
                        Matrix4x4 oldMatrix = Gizmos.matrix;
                        Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one * scale.x);
                        Gizmos.DrawSphere(c.center, c.radius);
                        if (fadeRadius > 0)
                            Gizmos.DrawWireSphere(c.center, c.radius + fadeRadius / scale.x);
                        Gizmos.matrix = oldMatrix;
                        break;
                    case MeshCollider c:
                        // Only convex mesh m_Colliders are allowed
                        if (!c.convex)
                            c.convex = true;

                        // Mesh pivot should be centered or this won't work
                        Gizmos.DrawMesh(c.sharedMesh);

                        // We don't display the Gizmo for fade distance mesh because the distances would be wrong
                        break;
                    default:
                        // Nothing for capsule (DrawCapsule isn't exposed in Gizmo), terrain, wheel and
                        // other m_Colliders...
                        break;
                }
            }
        }
#endif
    }
}
