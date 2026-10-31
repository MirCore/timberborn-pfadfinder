using System;
using Bindito.Core;
using HarmonyLib;
using Timberborn.ModManagerScene;
using UnityEngine;

namespace Pfadfinder
{
    /// <summary>Runs once when the game starts: applies the Harmony patches.</summary>
    public class ModStarter : IModStarter
    {
        public const string ModId = "Mirco.Pfadfinder"; // must match manifest.json
        public const string HarmonyId = ModId;

        public void StartMod(IModEnvironment modEnvironment)
        {
            try
            {
                new Harmony(HarmonyId).PatchAll(typeof(ModStarter).Assembly);
                Debug.Log("[Pfadfinder] Loaded. Drag a path to auto-route it; hold Alt for the vanilla L-shape.");
            }
            catch (Exception e)
            {
                Debug.LogError("[Pfadfinder] Failed to apply patches, the mod is inactive: " + e);
            }
        }
    }

    /// <summary>Registers the routing service in the game and the map editor.</summary>
    [Context("Game")]
    [Context("MapEditor")]
    public class PfadfinderConfigurator : Configurator
    {
        protected override void Configure()
        {
            Bind<RoutingService>().AsSingleton();
        }
    }

    /// <summary>Registers the settings wherever the Mod Settings menu can show them.</summary>
    [Context("MainMenu")]
    [Context("Game")]
    [Context("MapEditor")]
    public class PfadfinderSettingsConfigurator : Configurator
    {
        protected override void Configure()
        {
            Bind<PfadfinderSettings>().AsSingleton();
        }
    }
}
