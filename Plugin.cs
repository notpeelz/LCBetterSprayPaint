﻿using BepInEx;
using HarmonyLib;
using BepInEx.Logging;
using System.Reflection;
using System.IO;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;
using System;
using UnityEngine.InputSystem;
using System.Linq;
using BepInEx.Configuration;
using Unity.Netcode;
using Unity.Collections;
using UnityEngine.Rendering.HighDefinition;
using GameNetcodeStuff;

namespace BetterSprayPaint;

[BepInPlugin(modGUID, modName, modVersion)]
public class Plugin : BaseUnityPlugin {
    public const string modGUID = "taffyko.BetterSprayPaint";
    public const string modName = PluginInfo.PLUGIN_NAME;
    public const string modVersion = PluginInfo.PLUGIN_VERSION;
    
    private readonly Harmony harmony = new Harmony(modGUID);
    public static ManualLogSource? log;

    private static bool? infiniteTank;
    public static bool InfiniteTank => infiniteTank ?? true;
    private static bool? allowErasing;
    public static bool AllowErasing => allowErasing ?? true;
    private static float? volume;
    public static float Volume => volume ?? .2f;

    private void Awake() {
        log = BepInEx.Logging.Logger.CreateLogSource(modName);
        log.LogInfo($"Loading {modGUID}");

        // See: https://github.com/taffyko/LCNiceChat/issues/3
        if (bool.TryParse(
            Config.Bind<string?>("General", "AllowErasing", null, "(default: true) If true, holding tertiary action (default: E) while spraying allows you to erase. The host's setting applies to everyone in the lobby.").Value,
            out var _allowErasing
        )) { allowErasing = _allowErasing; }
        if (bool.TryParse(
            Config.Bind<string?>("General", "InfiniteTank", null, "(default: true) If true, the spray can has infinite uses. The host's setting applies to everyone in the lobby.").Value,
            out var _infiniteTank
        )) { infiniteTank = _infiniteTank; }
        if (float.TryParse(
            Config.Bind<string?>("General", "Volume", null, "(default: 0.2) Volume of spray paint sound effects.").Value,
            out var _volume
        )) { volume = _volume; }

        // Plugin startup logic
        harmony.PatchAll(Assembly.GetExecutingAssembly());
    }

    private void OnDestroy() {
        #if DEBUG
        log?.LogInfo($"Unloading {modGUID}");
        foreach (var instance in UnityEngine.Object.FindObjectsOfType<SprayPaintItem>()) {
            Patches.unload(instance);
        }
        harmony?.UnpatchSelf();
        Patches.sessionData = null;
        #endif
    }
}

public class SprayPaintItemExtraBehaviour: MonoBehaviour {
    public void OnDestroy() {
        TryGetComponent<SprayPaintItem>(out var instance);
        if (instance != null) Patches.unload(instance);
    }
}


[HarmonyPatch]
internal class Patches {


    public class SessionData {
        public bool handlerRegistered;
        public bool? allowErasing;
        public bool? infiniteTank;
    }

    const string msgConfigAllowErasing = $"{Plugin.modGUID}.ConfigAllowErasing";
    const string msgConfigInfiniteTank = $"{Plugin.modGUID}.ConfigInfiniteTank";
    public static SessionData? sessionData = null;
    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerControllerB), "Update")]
    public static void PlayerUpdate(PlayerControllerB __instance) {
        if (IsLocalPlayer(__instance)) {
            sessionData = new SessionData();
            if (__instance.NetworkManager.IsConnectedClient || __instance.NetworkManager.IsServer) {
                var msgManager = __instance.NetworkManager.CustomMessagingManager;
                if (!sessionData.handlerRegistered) {
                    msgManager.RegisterNamedMessageHandler(msgConfigInfiniteTank, (ulong clientId, FastBufferReader reader) => {
                        if (__instance.NetworkManager.IsServer) {
                            var writer = new FastBufferWriter(10, Allocator.Temp);
                            writer.WriteValue(Plugin.InfiniteTank);
                            msgManager.SendNamedMessage(msgConfigInfiniteTank, clientId, writer);
                        } else {
                            reader.ReadValue(out bool infiniteTank);
                            sessionData.infiniteTank = infiniteTank;
                        }
                    });
                    msgManager.RegisterNamedMessageHandler(msgConfigAllowErasing, (ulong clientId, FastBufferReader reader) => {
                        if (__instance.NetworkManager.IsServer) {
                            var writer = new FastBufferWriter(10, Allocator.Temp);
                            writer.WriteValue(Plugin.AllowErasing);
                            msgManager.SendNamedMessage(msgConfigAllowErasing, clientId, writer);
                        } else {
                            reader.ReadValue(out bool allowErasing);
                            sessionData.allowErasing = allowErasing;
                        }
                    });
                    sessionData.handlerRegistered = true;
                }
                if (sessionData.infiniteTank == null) {
                    if (!__instance.NetworkManager.IsServer) {
                        var writer = new FastBufferWriter(0, Allocator.Temp);
                        msgManager.SendNamedMessage(msgConfigInfiniteTank, NetworkManager.ServerClientId, writer);
                    }
                    sessionData.infiniteTank = Plugin.InfiniteTank; // assume default until response
                }
                if (sessionData.allowErasing == null) {
                    if (!__instance.NetworkManager.IsServer) {
                        var writer = new FastBufferWriter(0, Allocator.Temp);
                        msgManager.SendNamedMessage(msgConfigAllowErasing, NetworkManager.ServerClientId, writer);
                    }
                    sessionData.allowErasing = Plugin.AllowErasing; // assume default until response
                }
            } else {
                sessionData = null;
            }
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerControllerB), "OnDestroy")]
    public static void PlayerDestroy(PlayerControllerB __instance) {
        if (IsLocalPlayer(__instance)) {
            sessionData = null;
        }
        var msgManager = __instance.NetworkManager?.CustomMessagingManager;
        msgManager?.UnregisterNamedMessageHandler(msgConfigInfiniteTank);
        msgManager?.UnregisterNamedMessageHandler(msgConfigAllowErasing);

    }

    public static bool IsLocalPlayer(PlayerControllerB player) {
        return player == StartOfRound.Instance?.localPlayerController;
    }

    public static bool InfiniteTank => sessionData?.infiniteTank ?? Plugin.InfiniteTank;
    public static bool AllowErasing => sessionData?.allowErasing ?? Plugin.AllowErasing;
    
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SprayPaintItem), "LateUpdate")]
    public static void Update(SprayPaintItem __instance, ref float ___sprayCanTank, ref float ___sprayCanShakeMeter, ref AudioSource ___sprayAudio) {
        reload(__instance);
        // Spray more, forever, faster
        if (InfiniteTank) ___sprayCanTank = 1f;
        __instance.maxSprayPaintDecals = 4000;
        __instance.sprayIntervalSpeed = 0.01f;
        ___sprayAudio.volume = Plugin.Volume;

        if (__instance.playerHeldBy != null) {
            if (!IsLocalPlayer(__instance.playerHeldBy)) {
                // If someone else is holding the can, never let the shake meter fall below 50%
                // This fixes a de-sync where some clients think someone else's shake meter is empty
                // and fail to replicate their spray paint events because of it
                ___sprayCanShakeMeter = Math.Max(0.5f, ___sprayCanShakeMeter);
            }
            // Cancel can shake animation early
            var anim = __instance.playerHeldBy.playerBodyAnimator;
            foreach (var clipInfo in anim.GetCurrentAnimatorClipInfo(2)) {
                if (clipInfo.clip.name == "ShakeItem") {
                    var stateInfo = anim.GetCurrentAnimatorStateInfo(2);
                    if (stateInfo.normalizedTime > 0.1f) {
                        anim.Play("HoldOneHandedItem");
                    }
                }
            }
        }
    }

    public class CustomFields {
        public Material? sprayParticleMaterial;
        public Material? sprayEraseParticleMaterial;
        public int sprayPaintMask;
        public bool handlerRegistered;
        public SprayPaintItemExtraBehaviour? extraBehaviour;
    }

    public static InputAction? tertiaryUseAction;

    public static Dictionary<SprayPaintItem, CustomFields> fields = new();

    public static string msgErase(SprayPaintItem __instance) {
        return $"{Plugin.modGUID}.Erase.{__instance.NetworkObjectId}";
    }
    public static string msgSpray(SprayPaintItem __instance) {
        return $"{Plugin.modGUID}.Spray.{__instance.NetworkObjectId}";
    }
    public static void reload(SprayPaintItem __instance) {
        if (!fields.ContainsKey(__instance)) {
            fields[__instance] = new CustomFields();
        }
        var f = fields[__instance];
        if (tertiaryUseAction == null) {
            tertiaryUseAction = IngamePlayerSettings.Instance.playerInput.actions.FindAction("ItemTertiaryUse", false);
        }
        if (f.extraBehaviour == null) {
            if (!__instance.TryGetComponent<SprayPaintItemExtraBehaviour>(out f.extraBehaviour)) {
                f.extraBehaviour = __instance.gameObject.AddComponent<SprayPaintItemExtraBehaviour>();
            }
        }
        if (f.sprayPaintMask == 0) {
            f.sprayPaintMask = Traverse.Create(__instance).Field("sprayPaintMask").GetValue<int>();
        }
        if (f.sprayParticleMaterial == null) {
            var sprayCanMatsIndex = Traverse.Create(__instance).Field<int>("sprayCanMatsIndex").Value;
            f.sprayParticleMaterial = __instance.particleMats[sprayCanMatsIndex];
        }
        if (f.sprayEraseParticleMaterial == null) {
            f.sprayEraseParticleMaterial = new Material(f.sprayParticleMaterial);
            f.sprayEraseParticleMaterial.color = new Color(0.5f, 0.5f, 0.5f, 1f);
        }

        if (__instance.NetworkManager.IsConnectedClient || __instance.NetworkManager.IsServer) {
            if (!f.handlerRegistered) {
                var msgManager = __instance.NetworkManager.CustomMessagingManager;
                msgManager.RegisterNamedMessageHandler(msgErase(__instance), (ulong clientId, FastBufferReader reader) => {
                    if (AllowErasing) {
                        reader.ReadValueSafe(out Vector3 pos);

                        if (__instance.NetworkManager.IsServer && clientId != NetworkManager.ServerClientId) {
                            var writer = new FastBufferWriter(100, Allocator.Temp);
                            writer.WriteValueSafe(pos);
                            msgManager.SendNamedMessageToAll(msgErase(__instance), writer);
                        }
                        EraseSprayPaintAtPoint(__instance, pos);
                    }
                });
                msgManager.RegisterNamedMessageHandler(msgSpray(__instance), (ulong clientId, FastBufferReader reader) => {
                    reader.ReadValueSafe(out Vector3 sprayPos);
                    reader.ReadValueSafe(out Vector3 sprayRot);
                    if (__instance.NetworkManager.IsServer && clientId != NetworkManager.ServerClientId) {
                        var writer = new FastBufferWriter(100, Allocator.Temp);
                        writer.WriteValueSafe(sprayPos);
                        writer.WriteValueSafe(sprayRot);
                        msgManager.SendNamedMessageToAll(msgSpray(__instance), writer);
                    }
                    if (__instance.playerHeldBy != null && !IsLocalPlayer(__instance.playerHeldBy)) {
                        var result = AddSprayPaintLocal(__instance, sprayPos, sprayRot);
                    }
                });
                f.handlerRegistered = true;
            }
        }
    }

    public static void unload(SprayPaintItem __instance) {
        var msgManager = __instance.NetworkManager?.CustomMessagingManager;
        if (msgManager != null) {
            msgManager.UnregisterNamedMessageHandler(msgErase(__instance));
            msgManager.UnregisterNamedMessageHandler(msgSpray(__instance));
        }
        fields.Remove(__instance);
    }


    public static void EraseSprayPaintAtPoint(SprayPaintItem __instance, Vector3 pos) {
        foreach (GameObject decal in SprayPaintItem.sprayPaintDecals) {
            if (decal != null && Vector3.Distance(decal.transform.position, pos) < 0.5f) {
                decal.SetActive(false);
            }
        }
    }

    public static bool EraseSprayPaintLocal(SprayPaintItem __instance, Vector3 sprayPos, Vector3 sprayRot, out RaycastHit sprayHit) {
        var f = fields[__instance];
        Ray ray = new Ray(sprayPos, sprayRot);
        if (RaycastSkipPlayer(ray, out sprayHit, 6f, f.sprayPaintMask, QueryTriggerInteraction.Collide, __instance)) {
            EraseSprayPaintAtPoint(__instance, sprayHit.point);
            return true;
        } else {
            return false;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SprayPaintItem), "TrySpraying")]
    public static bool TrySpraying(SprayPaintItem __instance, ref bool __result, ref RaycastHit ___sprayHit) {
        var f = fields[__instance];
        var particleShape = __instance.sprayParticle.shape;
        var particleMain = __instance.sprayParticle.main;

        var sprayPos = GameNetworkManager.Instance.localPlayerController.gameplayCamera.transform.position;
        var sprayRot = GameNetworkManager.Instance.localPlayerController.gameplayCamera.transform.forward;

        if (AllowErasing && tertiaryUseAction != null && tertiaryUseAction.IsPressed()) {
            // "Erase" mode
            // Particles
            __instance.sprayParticle.GetComponent<ParticleSystemRenderer>().material = f.sprayEraseParticleMaterial;
            particleShape.angle = 5f;
            particleMain.startSpeed = 50f;
            particleMain.startLifetime = 0.1f;

            if (EraseSprayPaintLocal(__instance, sprayPos, sprayRot, out var sprayHit)) {
                __result = true;
                // RPC
                var msgManager = __instance.NetworkManager.CustomMessagingManager;
                var writer = new FastBufferWriter(100, Allocator.Temp);
                writer.WriteValueSafe(sprayHit.point);
                if (__instance.NetworkManager.IsServer) {
                    msgManager.SendNamedMessageToAll(msgErase(__instance), writer);
                } else {
                    msgManager.SendNamedMessage(msgErase(__instance), NetworkManager.ServerClientId, writer);
                }
            }
            return false;
        } else {
            // "Normal" mode
            // Particles
            __instance.sprayParticle.GetComponent<ParticleSystemRenderer>().material = f.sprayParticleMaterial;
            particleMain.startSpeed = 100f;
            particleMain.startLifetime = 0.05f;
            particleShape.angle = 0f;

            if (AddSprayPaintLocal(__instance, sprayPos, sprayRot)) {
                __result = true;
                // RPC
                var msgManager = __instance.NetworkManager.CustomMessagingManager;
                var writer = new FastBufferWriter(100, Allocator.Temp);
                writer.WriteValueSafe(sprayPos);
                writer.WriteValueSafe(sprayRot);
                if (__instance.NetworkManager.IsServer) {
                    msgManager.SendNamedMessageToAll(msgSpray(__instance), writer);
                } else {
                    msgManager.SendNamedMessage(msgSpray(__instance), NetworkManager.ServerClientId, writer);
                }
            }
            return false;
        }
    }

    // In the base game, a lot of raycasts fail because they hit the player rigidbody. This fixes that.
    public static bool RaycastSkipPlayer(Ray ray, out RaycastHit sprayHit, float _distance, int layerMask, QueryTriggerInteraction _queryTriggerInteraction, SprayPaintItem __instance) {
        var playerRigidbody = __instance.playerHeldBy?.playerRigidbody;
        if (playerRigidbody == null) {
            Plugin.log?.LogWarning("Player rigidbody is null");
        }
        bool result = false;
        RaycastHit sprayHitOut = default;
        foreach (var hit in Physics.RaycastAll(ray, 6f, layerMask, QueryTriggerInteraction.Ignore)) {
            if (playerRigidbody == null || hit.rigidbody != playerRigidbody) {
                if (!result) {
                    sprayHitOut = hit;
                    result = true;
                }
            }
        }
        sprayHit = sprayHitOut;
        return result;
    }

    static MethodInfo physicsRaycast = typeof(Physics).GetMethod(nameof(Physics.Raycast), new[] { typeof(Ray), typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(int), typeof(QueryTriggerInteraction) });
    static MethodInfo raycastSkipPlayer = typeof(Patches).GetMethod(nameof(RaycastSkipPlayer));

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(SprayPaintItem), "AddSprayPaintLocal")]
    private static IEnumerable<CodeInstruction> transpiler_AddSprayPaintLocal(IEnumerable<CodeInstruction> instructions) {
        var foundMinNextDecalDistance = false;
        var foundRaycastCall = false;
        foreach (var instruction in instructions) {
            if (!foundMinNextDecalDistance && instruction.opcode == OpCodes.Ldc_R4 && (float)instruction.operand == 0.175f) {
                foundMinNextDecalDistance = true;
                // Reduce the minimum movement needed from the last position before you are allowed to spray a new decal
                yield return new CodeInstruction(OpCodes.Ldc_R4, 0.001f);
            } else if (!foundRaycastCall && instruction.opcode == OpCodes.Call && instruction.operand == (object)physicsRaycast) {
                foundRaycastCall = true;
                // Replace the call to Physics.Raycast
                yield return new CodeInstruction(OpCodes.Ldarg_0); // pass instance as extra parameter
                yield return new CodeInstruction(OpCodes.Call, raycastSkipPlayer);
            }
            else {
                yield return instruction;
            }
        }
    }

    [HarmonyReversePatch]
    [HarmonyPatch(typeof(SprayPaintItem), "AddSprayPaintLocal")]
    public static bool _AddSprayPaintLocal(object instance, Vector3 sprayPos, Vector3 sprayRot) {
        IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            var foundMinNextDecalDistance = false;
            var foundRaycastCall = false;
            foreach (var instruction in instructions) {
                if (!foundMinNextDecalDistance && instruction.opcode == OpCodes.Ldc_R4 && (float)instruction.operand == 0.175f) {
                    foundMinNextDecalDistance = true;
                    // Reduce the minimum movement needed from the last position before you are allowed to spray a new decal
                    yield return new CodeInstruction(OpCodes.Ldc_R4, 0.001f);
                } else if (!foundRaycastCall && instruction.opcode == OpCodes.Call && instruction.operand == (object)physicsRaycast) {
                    foundRaycastCall = true;
                    // Replace the call to Physics.Raycast
                    yield return new CodeInstruction(OpCodes.Ldarg_0); // pass instance as extra parameter
                    yield return new CodeInstruction(OpCodes.Call, raycastSkipPlayer);
                }
                else {
                    yield return instruction;
                }
            }
        }
        _ = Transpiler(null!);
        return default;
    }

    public static bool AddSprayPaintLocal(SprayPaintItem instance, Vector3 sprayPos, Vector3 sprayRot) {
        var result = _AddSprayPaintLocal(instance, sprayPos, sprayRot);
        // Use the raycast normal to orient the decal so that decals are no longer distorted when spraying at an angle
        if (result && SprayPaintItem.sprayPaintDecals.Count > SprayPaintItem.sprayPaintDecalsIndex) {
            var gameObject = SprayPaintItem.sprayPaintDecals[SprayPaintItem.sprayPaintDecalsIndex];
            var sprayHit = Traverse.Create(instance).Field<RaycastHit>("sprayHit").Value;
            gameObject.transform.forward = -sprayHit.normal;
            #if DEBUG
            gameObject.name = $"SprayDecal_{SprayPaintItem.sprayPaintDecalsIndex}";
            Plugin.log?.LogInfo($"{gameObject.name} added to {gameObject.transform.parent.name} at{gameObject.transform.position}");
            #endif
            // Spraypaint had a netcode issue where some decals don't show up on remote clients,
            // because their DecalProjector.enabled is set to false. Make sure it's set to true whenever a decal is added.
            var projector = gameObject.GetComponent<DecalProjector>();
            projector.enabled = true;
        }
        return result;
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(SprayPaintItem), "ItemInteractLeftRight")]
    private static IEnumerable<CodeInstruction> transpiler_ItemInteractLeftRight(IEnumerable<CodeInstruction> instructions) {
        var foundShakeRestoreAmount = false;
        foreach (var instruction in instructions) {
            if (!foundShakeRestoreAmount && instruction.opcode == OpCodes.Ldc_R4 && (float)instruction.operand == 0.15f) {
                // Make shaking restore double the normal amount on the "shake meter"
                foundShakeRestoreAmount = true;
                yield return new CodeInstruction(OpCodes.Ldc_R4, 0.30f);
            } else {
                yield return instruction;
            }
        }
    }
}