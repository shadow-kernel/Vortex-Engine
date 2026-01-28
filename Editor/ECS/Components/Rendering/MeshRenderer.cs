using System;
using System.Runtime.Serialization;
using Editor.DllWrapper;
using Editor.Utilities;

namespace Editor.ECS.Components.Rendering
{
    /// <summary>
    /// MeshRenderer-Komponente für 3D-Mesh-Darstellung.
    /// </summary>
    [DataContract(Name = "MeshRenderer", Namespace = "")]
    public class MeshRenderer : Component
    {
        private string _meshPath;
        private string _materialPath;
        private string _texturePath;
        private string _shaderPath;
        private bool _castShadows = true;
        private bool _receiveShadows = true;
        private int _renderLayer;
        
        // Material color properties
        private float _colorR = 0.7f;
        private float _colorG = 0.7f;
        private float _colorB = 0.7f;
        private float _colorA = 1.0f;
        
        // PBR properties
        private float _metallic = 0.0f;
        private float _roughness = 0.5f;
        private float _normalStrength = 1.0f;

        [IgnoreDataMember]
        private long _meshHandle = ID.INVALID_ID;

        [IgnoreDataMember]
        private long _materialHandle = ID.INVALID_ID;

        [IgnoreDataMember]
        private long _rendererHandle = ID.INVALID_ID;

        [IgnoreDataMember]
        private long _textureHandle = ID.INVALID_ID;

        [IgnoreDataMember]
        private long _shaderHandle = ID.INVALID_ID;

        public override string DisplayName => "Mesh Renderer";
        public override string IconCode => "\uE809";
        public override string IconColor => "#4EC9B0";

        /// <summary>
        /// Pfad zur Mesh-Datei
        /// </summary>
        [DataMember(Name = "meshPath", Order = 10)]
        public string MeshPath
        {
            get => _meshPath;
            set
            {
                if (SetProperty(ref _meshPath, value, nameof(MeshPath)))
                {
                    ReloadMeshHandle();
                    SyncToEngine();
                }
            }
        }

        /// <summary>
        /// Pfad zur Material-Datei
        /// </summary>
        [DataMember(Name = "materialPath", Order = 11)]
        public string MaterialPath
        {
            get => _materialPath;
            set
            {
                if (SetProperty(ref _materialPath, value, nameof(MaterialPath)))
                {
                    ReloadMaterialHandle();
                    SyncToEngine();
                }
            }
        }

        /// <summary>
        /// Ob das Mesh Schatten wirft
        /// </summary>
        [DataMember(Name = "castShadows", Order = 12)]
        public bool CastShadows
        {
            get => _castShadows;
            set => SetProperty(ref _castShadows, value, nameof(CastShadows));
        }

        /// <summary>
        /// Ob das Mesh Schatten empfängt
        /// </summary>
        [DataMember(Name = "receiveShadows", Order = 13)]
        public bool ReceiveShadows
        {
            get => _receiveShadows;
            set => SetProperty(ref _receiveShadows, value, nameof(ReceiveShadows));
        }

        /// <summary>
        /// Render-Layer für Culling
        /// </summary>
        [DataMember(Name = "renderLayer", Order = 14)]
        public int RenderLayer
        {
            get => _renderLayer;
            set => SetProperty(ref _renderLayer, value, nameof(RenderLayer));
        }

        /// <summary>
        /// Material base color - Red component (0-1)
        /// </summary>
        [DataMember(Name = "colorR", Order = 15)]
        public float ColorR
        {
            get => _colorR;
            set => SetProperty(ref _colorR, Clamp01(value), nameof(ColorR));
        }

        /// <summary>
        /// Material base color - Green component (0-1)
        /// </summary>
        [DataMember(Name = "colorG", Order = 16)]
        public float ColorG
        {
            get => _colorG;
            set => SetProperty(ref _colorG, Clamp01(value), nameof(ColorG));
        }

        /// <summary>
        /// Material base color - Blue component (0-1)
        /// </summary>
        [DataMember(Name = "colorB", Order = 17)]
        public float ColorB
        {
            get => _colorB;
            set => SetProperty(ref _colorB, Clamp01(value), nameof(ColorB));
        }

        /// <summary>
        /// Material base color - Alpha component (0-1)
        /// </summary>
        [DataMember(Name = "colorA", Order = 18)]
        public float ColorA
        {
            get => _colorA;
            set => SetProperty(ref _colorA, Clamp01(value), nameof(ColorA));
        }

        /// <summary>
        /// Metallic value for PBR rendering (0-1)
        /// </summary>
        [DataMember(Name = "metallic", Order = 19)]
        public float Metallic
        {
            get => _metallic;
            set => SetProperty(ref _metallic, Clamp01(value), nameof(Metallic));
        }

        /// <summary>
        /// Roughness value for PBR rendering (0-1)
        /// </summary>
        [DataMember(Name = "roughness", Order = 20)]
        public float Roughness
        {
            get => _roughness;
            set => SetProperty(ref _roughness, Clamp01(value), nameof(Roughness));
        }

        /// <summary>
        /// Normal map strength (0-2)
        /// </summary>
        [DataMember(Name = "normalStrength", Order = 21)]
        public float NormalStrength
        {
            get => _normalStrength;
            set => SetProperty(ref _normalStrength, Math.Max(0, Math.Min(2, value)), nameof(NormalStrength));
        }

        /// <summary>
        /// Path to the diffuse/albedo texture file (persisted for reload after restart)
        /// </summary>
        [DataMember(Name = "texturePath", Order = 22)]
        public string TexturePath
        {
            get => _texturePath;
            set
            {
                if (SetProperty(ref _texturePath, value, nameof(TexturePath)))
                {
                    ReloadTextureAndMaterial();
                }
            }
        }

        /// <summary>
        /// Path to the custom shader file (.hlsl)
        /// </summary>
        [DataMember(Name = "shaderPath", Order = 23)]
        public string ShaderPath
        {
            get => _shaderPath;
            set
            {
                if (SetProperty(ref _shaderPath, value, nameof(ShaderPath)))
                {
                    ReloadShaderHandle();
                }
            }
        }

        /// <summary>
        /// Gets or sets the native material handle (for imported models with textures)
        /// </summary>
        [IgnoreDataMember]
        public long MaterialHandle
        {
            get => _materialHandle;
            set => _materialHandle = value;
        }

        /// <summary>
        /// Indicates if this mesh has a pre-loaded material with textures
        /// </summary>
        [IgnoreDataMember]
        public bool HasImportedMaterial => _materialHandle != ID.INVALID_ID && _materialHandle >= 0;

        public MeshRenderer() : base() { }
        public MeshRenderer(GameEntity entity) : base(entity) { }
        public MeshRenderer(GameEntity entity, string meshPath) : base(entity)
        {
            MeshPath = meshPath;
        }

        private static float Clamp01(float value)
        {
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }

        [OnDeserialized]
        internal void OnDeserializedMeshRenderer(StreamingContext context)
        {
            System.Diagnostics.Debug.WriteLine($"[MeshRenderer] OnDeserialized - MeshPath={_meshPath}, TexturePath={_texturePath}");
            ReloadMeshHandle();
            ReloadMaterialHandle();
            ReloadTextureAndMaterial();
            ReloadShaderHandle();
        }

        private void ReloadMeshHandle()
        {
            if (_meshHandle != ID.INVALID_ID)
            {
                VortexAPI.UnloadResourceHandle(_meshHandle);
                _meshHandle = ID.INVALID_ID;
            }

            if (!string.IsNullOrEmpty(_meshPath))
            {
                _meshHandle = VortexAPI.LoadMeshResource(_meshPath);
            }
        }

        private void ReloadShaderHandle()
        {
            if (_shaderHandle != ID.INVALID_ID)
            {
                VortexAPI.UnloadResourceHandle(_shaderHandle);
                _shaderHandle = ID.INVALID_ID;
            }

            if (!string.IsNullOrEmpty(_shaderPath))
            {
                _shaderHandle = VortexAPI.LoadShaderResource(_shaderPath);
            }
        }


        private void ReloadMaterialHandle()
        {
            if (_materialHandle != ID.INVALID_ID)
            {
                VortexAPI.UnloadResourceHandle(_materialHandle);
                _materialHandle = ID.INVALID_ID;
            }

            if (!string.IsNullOrEmpty(_materialPath))
            {
                _materialHandle = VortexAPI.LoadMaterialResource(_materialPath);
            }
        }

        /// <summary>
        /// Reloads the texture and creates/updates material with texture binding.
        /// Called on deserialization to restore textures after restart.
        /// </summary>
        private void ReloadTextureAndMaterial()
        {
            if (string.IsNullOrEmpty(_texturePath))
                return;

            // Schedule the texture reload for after project is fully loaded
            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => DoReloadTextureAndMaterial()));
        }

        private void DoReloadTextureAndMaterial()
        {
            System.Diagnostics.Debug.WriteLine($"[MeshRenderer] DoReloadTextureAndMaterial START - TexturePath={_texturePath}");
            
            if (string.IsNullOrEmpty(_texturePath))
            {
                System.Diagnostics.Debug.WriteLine($"[MeshRenderer] TexturePath is empty, skipping texture reload");
                return;
            }

            // Get full path to texture
            var projectPath = Core.Data.ProjectData.Current?.Path;
            string fullTexturePath = _texturePath;
            
            if (!System.IO.Path.IsPathRooted(_texturePath) && !string.IsNullOrEmpty(projectPath))
            {
                fullTexturePath = System.IO.Path.Combine(projectPath, _texturePath);
            }

            if (!System.IO.File.Exists(fullTexturePath))
            {
                System.Diagnostics.Debug.WriteLine($"[MeshRenderer] Texture file not found: {fullTexturePath}");
                return;
            }

            try
            {
                // Import the texture
                long textureId = VortexAPI.ImportTextureFromFile(fullTexturePath);
                if (textureId < 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[MeshRenderer] Failed to import texture: {fullTexturePath}");
                    return;
                }

                _textureHandle = textureId;

                // Create or use existing material
                if (_materialHandle == ID.INVALID_ID || _materialHandle < 0)
                {
                    _materialHandle = VortexAPI.CreateNewMaterial();
                    if (_materialHandle < 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MeshRenderer] Failed to create material");
                        return;
                    }
                    VortexAPI.SetMaterialBaseColor(_materialHandle, 0.9f, 0.9f, 0.9f, 1.0f);
                }

                // Bind texture to material
                VortexAPI.SetMaterialAlbedoTexture(_materialHandle, textureId);
                
                // Register in SceneRenderService cache for consistent rendering
                if (!string.IsNullOrEmpty(_meshPath))
                {
                    Core.Services.SceneRenderService.RegisterMaterialForMeshPath(_meshPath, _materialHandle);
                }

                System.Diagnostics.Debug.WriteLine($"[MeshRenderer] Restored texture {fullTexturePath} to material {_materialHandle}");
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MeshRenderer] Error reloading texture: {ex.Message}");
            }
        }

        /// <summary>
        /// Synchronisiert die MeshRenderer-Daten zur Engine.
        /// Wird aufgerufen wenn Entity aktiv wird oder Mesh/Material sich ändert.
        /// </summary>
        internal void SyncToEngine()
        {
            // Kein Entity oder Entity nicht in Engine registriert
            if (Entity == null || !ID.IsValid(Entity.EntityId))
                return;

            // Kein Mesh geladen
            if (!ID.IsValid(_meshHandle))
            {
                // Falls schon ein Renderer existiert, entfernen
                if (ID.IsValid(_rendererHandle))
                {
                    VortexAPI.DestroyMeshRendererComponent(_rendererHandle);
                    _rendererHandle = ID.INVALID_ID;
                }
                return;
            }

            if (!ID.IsValid(_rendererHandle))
            {
                // Erstelle neuen MeshRenderer in der Engine
                _rendererHandle = VortexAPI.CreateMeshRendererComponent(
                    Entity.EntityId, 
                    _meshHandle, 
                    _materialHandle);
            }
            else
            {
                // Aktualisiere bestehenden MeshRenderer
                VortexAPI.UpdateMeshRendererMesh(_rendererHandle, _meshHandle);
                if (ID.IsValid(_materialHandle))
                {
                    VortexAPI.UpdateMeshRendererMaterial(_rendererHandle, _materialHandle);
                }
            }
        }

        /// <summary>
        /// Entfernt die MeshRenderer-Komponente aus der Engine.
        /// </summary>
        internal void RemoveFromEngine()
        {
            if (ID.IsValid(_rendererHandle))
            {
                VortexAPI.DestroyMeshRendererComponent(_rendererHandle);
                _rendererHandle = ID.INVALID_ID;
            }
        }
    }

    /// <summary>
    /// SpriteRenderer-Komponente für 2D-Sprite-Darstellung.
    /// </summary>
    [DataContract(Name = "SpriteRenderer", Namespace = "")]
    public class SpriteRenderer : Component
    {
        private string _spritePath;
        private float _colorR = 1f;
        private float _colorG = 1f;
        private float _colorB = 1f;
        private float _colorA = 1f;
        private int _sortingOrder;
        private bool _flipX;
        private bool _flipY;

        [IgnoreDataMember]
        private long _spriteHandle = ID.INVALID_ID;

        public override string DisplayName => "Sprite Renderer";
        public override string IconCode => "\uE8B9";
        public override string IconColor => "#C586C0";

        [DataMember(Name = "spritePath", Order = 10)]
        public string SpritePath
        {
            get => _spritePath;
            set
            {
                if (SetProperty(ref _spritePath, value, nameof(SpritePath)))
                {
                    ReloadSpriteHandle();
                }
            }
        }

        [DataMember(Name = "colorR", Order = 11)]
        public float ColorR
        {
            get => _colorR;
            set => SetProperty(ref _colorR, value, nameof(ColorR));
        }

        [DataMember(Name = "colorG", Order = 12)]
        public float ColorG
        {
            get => _colorG;
            set => SetProperty(ref _colorG, value, nameof(ColorG));
        }

        [DataMember(Name = "colorB", Order = 13)]
        public float ColorB
        {
            get => _colorB;
            set => SetProperty(ref _colorB, value, nameof(ColorB));
        }

        [DataMember(Name = "colorA", Order = 14)]
        public float ColorA
        {
            get => _colorA;
            set => SetProperty(ref _colorA, value, nameof(ColorA));
        }

        [DataMember(Name = "sortingOrder", Order = 15)]
        public int SortingOrder
        {
            get => _sortingOrder;
            set => SetProperty(ref _sortingOrder, value, nameof(SortingOrder));
        }

        [DataMember(Name = "flipX", Order = 16)]
        public bool FlipX
        {
            get => _flipX;
            set => SetProperty(ref _flipX, value, nameof(FlipX));
        }

        [DataMember(Name = "flipY", Order = 17)]
        public bool FlipY
        {
            get => _flipY;
            set => SetProperty(ref _flipY, value, nameof(FlipY));
        }

        public SpriteRenderer() : base() { }
        public SpriteRenderer(GameEntity entity) : base(entity) { }

        [OnDeserialized]
        internal void OnDeserializedSpriteRenderer(StreamingContext context)
        {
            ReloadSpriteHandle();
        }

        private void ReloadSpriteHandle()
        {
            if (_spriteHandle != ID.INVALID_ID)
            {
                VortexAPI.UnloadResourceHandle(_spriteHandle);
                _spriteHandle = ID.INVALID_ID;
            }

            if (!string.IsNullOrEmpty(_spritePath))
            {
                _spriteHandle = VortexAPI.LoadTextureResource(_spritePath);
            }
        }
    }
}
