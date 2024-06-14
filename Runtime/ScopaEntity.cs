using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;

namespace Scopa {
    /// <summary> 
    /// General container to hold entity data, and coordinate targeting / input flow across multiple components.
    /// To configure your custom components with entity data, you have three options:
    /// 1. call GetComponent<ScopaEntity>().TryGet*() to poll for data at runtime, 
    /// 2. implement IScopaEntity and use OnScopaImport()
    /// 3. use [BindFgd] to automatically bind FGD entry + entity data at import time. 
    /// </summary>
    [SelectionBase]
    public class ScopaEntity : MonoBehaviour, IScopaEntityLogic, IScopaEntityData, IScopaEntityImport
    {


        #region Entity Logic

        /// <summary>
        /// This is the character to use in your target text boxes to indicate the names of multiple targets you'd want to hit.
        /// </summary>
        const string TARGET_LIST_CHAR = ";";

        /// <summary> a big global dictionary lookup to match entities to their names. Note that multiple entities can share the same name. </summary>
        public static Dictionary<string, List<ScopaEntity>> entityLookup = new Dictionary<string, List<ScopaEntity>>();

        /// <summary> The name of this entity, for map triggering purposes.</summary>
        [Tooltip("The name of this entity, so other entities can target it.")]
        [BindFgd("targetname", BindFgd.VarType.String, "Name")] 
        public string entityName;

        /// <summary> When this entity activates, then also activate this entity(s).</summary>
        [Tooltip("When this entity activates, then also activate this entity(s) by name.")]
        [BindFgd("target", BindFgd.VarType.String, "Target")] 
        public string entityTarget;

        /// <summary> When this entity activates, the seconds before it fires its target(s). </summary>
        [Tooltip("When this entity activates, the seconds before it fires its target.")]
        [BindFgd("delay", BindFgd.VarType.Float, "Delay Before Target")] 
        public float activationDelay = 0f;

        /// <summary> internal timer for when to activate target(s), based on delay </summary>
        protected float targetDelayRemaining = -1;
        protected ScopaEntity lastActivator;

        /// <summary> to be able to activate, there must be no activation or resetting in progress AND the entity must be unlocked </summary>
        public bool canActivate { get { return targetDelayRemaining <= 0 && resetDelayRemaining <= 0 && !isLocked; }}

        /// <summary> When this entity activates, then also LOCK this entity(s).</summary>
        [Tooltip("When this entity activates, then also LOCK this entity(s) by name.")]
        [BindFgd("locktarget", BindFgd.VarType.String, "Lock Target")] 
        public string lockTarget;

        /// <summary> When this entity activates, then also UNLOCK this entity(s).</summary>
        [Tooltip("When this entity activates, then also UNLOCK this entity(s) by name.")]
        [BindFgd("unlocktarget", BindFgd.VarType.String, "Unlock Target")] 
        public string unlockTarget;

        /// <summary> When this entity activates, then kill / DESTROY this entity(s).</summary>
        [Tooltip("When this entity activates, then also kill / DESTROY this entity(s) by name.")]
        [BindFgd("killtarget", BindFgd.VarType.String, "Kill / Destroy Target")] 
        public string killTarget;

        /// <summary> After this entity activates, the seconds to wait before reseting itself. -1 = never reset. </summary>
        [Tooltip("After this entity activates, the seconds to wait before reseting itself. -1 = never reset.")]
        [BindFgd("wait", BindFgd.VarType.Float, "Wait Before Reset")]
        public float waitReset = 3f;

        /// <summary> internal timer for when to reset itself, based on waitReset </summary>
        protected float resetDelayRemaining = -1;

        /// <summary> When an entity is locked, it cannot activate or trigger. See locktarget and unlocktarget. </summary>
        [Tooltip("When an entity is locked, it cannot activate or trigger. See locktarget and unlocktarget.")]
        [BindFgd("locked", BindFgd.VarType.Bool, "Is Locked")]
        public bool isLocked = false;

        /// <summary> When sending out events (OnActivate, OnReset, etc.) to IScopaEntityLogic components in children, cache the results of GetComponentsInChildren() for better perf. If this game object's children do not change, set this to true. </summary>
        [Tooltip("When sending out events (OnActivate, OnReset, etc.) to IScopaEntityLogic components in children, cache the results of GetComponentsInChildren() for better perf. If this game object's children do not change, set this to true.")]
        public bool cacheChildrenForDispatch = true;
        IScopaEntityLogic[] allTargets;

        List<string> _cachedTargets;
        List<string> cachedTargets
        {
            get
            { 
                if (_cachedTargets == null && !string.IsNullOrWhiteSpace(entityTarget))
                {
                    _cachedTargets = entityTarget.Split(TARGET_LIST_CHAR).Select(x => SanitizeString(x)).ToList();
                }
                return _cachedTargets;
            }
        }

        List<string> _cachedLockTargets;
        List<string> cachedLockTargets
        {
            get
            {
                if (_cachedLockTargets == null && !string.IsNullOrWhiteSpace(entityTarget))
                {
                    _cachedLockTargets = entityTarget.Split(TARGET_LIST_CHAR).Select(x => SanitizeString(x)).ToList();
                }
                return _cachedLockTargets;
            }
        }

        List<string> _cachedUnlockTargets;
        List<string> cachedUnlockTargets
        {
            get
            {
                if (_cachedUnlockTargets == null && !string.IsNullOrWhiteSpace(entityTarget))
                {
                    _cachedUnlockTargets = entityTarget.Split(TARGET_LIST_CHAR).Select(x => SanitizeString(x)).ToList();
                }
                return _cachedUnlockTargets;
            }
        }

        List<string> _cachedKillTargets;
        List<string> cachedKillTargets
        {
            get
            {
                if (_cachedKillTargets == null && !string.IsNullOrWhiteSpace(entityTarget))
                {
                    _cachedKillTargets = entityTarget.Split(TARGET_LIST_CHAR).Select(x => SanitizeString(x)).ToList();
                }
                return _cachedKillTargets;
            }
        }

        static string SanitizeString(string input)
        {
            return input.Trim().Normalize();
        }

        protected void Awake() {
            // if this entity has a entityName, it needs to register itself so other entities can target it
            if (Application.isPlaying && !string.IsNullOrWhiteSpace(entityName))
            { 
                if (!entityLookup.ContainsKey(entityName))
                {
                    entityLookup.Add(entityName, new List<ScopaEntity>());
                }

                if (!entityLookup[entityName].Contains(this))
                { 
                    entityLookup[entityName].Add(this);
                }
            }

            OnAwake();
        }

        protected virtual void OnAwake() {}

        protected void Update() {
            // target timer
            if ( targetDelayRemaining >= 0 && !isLocked ) 
            {
                targetDelayRemaining -= Time.deltaTime;
                if ( canActivate ) 
                {
                    Activate();
                    FireTarget();
                }
            } // reset cooldown timer
            else if ( resetDelayRemaining > 0 && waitReset >= 0 && !isLocked) 
            {
                resetDelayRemaining -= Time.deltaTime;
                if ( resetDelayRemaining <= 0 )
                { 
                    Reset();
                }
            }

            OnUpdate();
        }

        protected virtual void OnUpdate() { }

        protected void FireTarget() 
        {
            // normal target
            foreach (var target in cachedTargets)
            { 
                if ( !string.IsNullOrWhiteSpace(target) && entityLookup.ContainsKey(target) ) 
                {
                    foreach( var targetEntity in entityLookup[target] ) 
                    {
                        targetEntity.TryActivate(this);
                    }
                }
            }

            foreach (var target in cachedLockTargets)
            {
                // lock target
                if (!string.IsNullOrWhiteSpace(target) && entityLookup.ContainsKey(target))
                {
                    foreach (var targetEntity in entityLookup[target])
                    {
                        targetEntity.Lock();
                    }
                }
            }

            // unlock target
            foreach (var target in cachedUnlockTargets)
            {
                // lock target
                if (!string.IsNullOrWhiteSpace(target) && entityLookup.ContainsKey(target))
                {
                    foreach (var targetEntity in entityLookup[target])
                    {
                        targetEntity.Unlock();
                    }
                }
            }

            foreach (var target in cachedKillTargets)
            {
                // lock target
                if (!string.IsNullOrWhiteSpace(target) && entityLookup.ContainsKey(target))
                {
                    foreach (var targetEntity in entityLookup[target])
                    {
                        targetEntity.Lock();
                    }
                }
            }
        }

        public void Reset() {
            // notify everything about reset
            CacheTargetsIfNeeded();
            foreach( var target in allTargets ) 
            {
                target.OnEntityReset();
            }
            resetDelayRemaining = -1;
            targetDelayRemaining = -1;
        }

        /// <summary> The main function to use when activating an entity;
        /// returns false if entity (a) is already activating, or (b) hasn't reset yet, or (c) is locked. 
        /// Pass in 'force = true' to ignore these checks and force activation. 
        /// Note that 'activator' might be null. </summary>
        public bool TryActivate(ScopaEntity activator, bool force = false) 
        {
            if ( !canActivate && !force ) return false;

            lastActivator = activator;
            targetDelayRemaining = activationDelay;
            resetDelayRemaining = waitReset + 0.001f; // add small reset delay to ensure 1 frame between activations
            Activate();
            return true;
        }

        public void TryActivate(bool force = false) 
        {
            TryActivate(null, force);
        }
        
        protected void CacheTargetsIfNeeded() 
        {
            // ignore costly GetComponentsInChildren call
            if ( cacheChildrenForDispatch && allTargets != null ) return;
            
            allTargets = GetComponentsInChildren<IScopaEntityLogic>();
        }

        public void Activate() 
        {
            Debug.Log(gameObject.name + " is activating!");
            CacheTargetsIfNeeded();
            foreach( var target in allTargets ) 
            {
                target.OnEntityActivate(lastActivator);
            }
        }

        public void Lock() 
        {
            isLocked = true;
            CacheTargetsIfNeeded();
            foreach( var target in allTargets ) 
            {
                target.OnEntityLocked();
            }
        }

        public void Unlock() 
        {
            isLocked = false;
            CacheTargetsIfNeeded();
            foreach( var target in allTargets ) 
            {
                target.OnEntityUnlocked();
            }
        }

        public void Kill() 
        {
            CacheTargetsIfNeeded();
            foreach( var target in allTargets ) 
            {
                target.OnEntityKilled();
            }
            entityLookup[entityName].Remove(this);
            Destroy( this.gameObject );
        }

        #endregion

        #region EntityData

        [SerializeField] ScopaEntityData _entityData;
        public ScopaEntityData entityData {
            get => _entityData;
            set {
                _entityData = value;
            }
        }

        /// <summary> the entity type, e.g. func_wall, info_player_start, worldspawn </summary>
        public string className => entityData.ClassName;

        /// <summary> raw bitmask of the true/false flags set for each entity; you may prefer to use GetSpawnFlags() instead </summary>
        public int spawnFlags => entityData.SpawnFlags;

        /// <summary> convenience function to return spawn flags as a set of booleans; 24 is the default max limit for Quake 1 </summary>
        public bool[] GetSpawnFlags(int maxFlagCount = 24) => entityData.GetSpawnFlags(maxFlagCount);

        /// <summary> the entity number, based on the order it was parsed within the map file </summary>
        public int ScopaEntityID => entityData.ID;

        /// <summary> parses property as an string, essentially the raw data; empty or whitespace will return false </summary>
        public bool TryGetString(string propertyKey, out string text) => entityData.TryGetString(propertyKey, out text);

        /// <summary> parses property as a bool ("0" or "False" is false, anything else is true); empty or whitespace will make this function return false </summary>
        public bool TryGetBool(string propertyKey, out bool boolValue) => entityData.TryGetBool(propertyKey, out boolValue);

        /// <summary> parses property as an int; empty or whitespace will return false </summary>
        public bool TryGetInt(string propertyKey, out int num) => entityData.TryGetInt(propertyKey, out num);

        /// <summary> parses property as an int affected by scaling factor, by default 0.03125 (32 map units = 1 Unity meter); empty or whitespace will return false </summary>
        public bool TryGetIntScaled(string propertyKey, out int num, float scalar = 0.03125f) => entityData.TryGetIntScaled(propertyKey, out num, scalar);

        /// <summary> parses property as a float; empty or whitespace will return false </summary>
        public bool TryGetFloat(string propertyKey, out float num) => entityData.TryGetFloat(propertyKey, out num);

        /// <summary> parses property as a float affected by scaling factor, by default 0.03125 (32 map units = 1 Unity meter); empty or whitespace will return false </summary>
        public bool TryGetFloatScaled(string propertyKey, out float num, float scalar = 0.03125f) => entityData.TryGetFloatScaled(propertyKey, out num, scalar);

        /// <summary> parses an entity property as a Quake-style single number rotation; > 0 is negative yaw + 90 degrees, -1 is up, -2 is down; empty or whitespace will return false / Quaternion.identity </summary>
        public bool TryGetAngleSingle(string propertyKey, out Quaternion rotation, bool verbose = false) => entityData.TryGetAngleSingle(propertyKey, out rotation, verbose);

        /// <summary> parses property as an unscaled Vector3 (swizzled for Unity in XZY format), if it exists as a valid Vector3; empty or whitespace will return false </summary>
        public bool TryGetVector3Unscaled(string propertyKey, out Vector3 vec) => entityData.TryGetVector3Unscaled(propertyKey, out vec);

        /// <summary> parses property as an SCALED Vector3 (+ swizzled for Unity in XZY format) at a default scale of 0.03125 (32 map units = 1 Unity meter), if it exists as a valid Vector3; empty or whitespace will return false </summary>
        public bool TryGetVector3Scaled(string propertyKey, out Vector3 vec, float scalar = 0.03125f) => entityData.TryGetVector3Scaled(propertyKey, out vec, scalar);

        /// <summary> parses property as an unscaled Vector4 (and NOT swizzled), if it exists as a valid Vector4; empty or whitespace will return false </summary>
        public bool TryGetVector4Unscaled(string propertyKey, out Vector4 vec) => entityData.TryGetVector4Unscaled(propertyKey, out vec);

        /// <summary> parses property as an RGB Color (and try to detect if it's 0.0-1.0 or 0-255); empty or whitespace will return false and Color.black</summary>
        public bool TryGetColorRGB(string propertyKey, out Color color) => entityData.TryGetColorRGB(propertyKey, out color);

        /// <summary> parses property as an RGBA Color (and try to detect if it's 0.0-1.0 or 0-255); empty or whitespace will return false and Color.black</summary>
        public bool TryGetColorRGBA(string propertyKey, out Color color) => entityData.TryGetColorRGBA(propertyKey, out color);

        /// <summary> parses property as an RGB Color (0-255) with a fourth number as light intensity scalar (255 = 1.0f), common as the Half-Life 1 GoldSrc / Half-Life 2 Source light color format (e.g. "255 255 255 200"); empty or whitespace will return false and Color.black and intensity 0.0</summary>
        public bool TryGetColorLight(string propertyKey, out Color color, out float intensity) => entityData.TryGetColorLight(propertyKey, out color, out intensity);
        
        /// <summary> returns a string of all entity data, including all properties and keyvalue pairs</summary>
        public override string ToString() => entityData.ToString();

        #endregion
    }

}
