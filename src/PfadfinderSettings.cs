using ModSettings.Core;
using Timberborn.Modding;
using Timberborn.SettingsSystem;

namespace Pfadfinder
{
    /// <summary>The mod's options, shown by the "Mod Settings" mod (eMka.ModSettings) and saved with the game settings.</summary>
    public class PfadfinderSettings : ModSettingsOwner
    {
        /// <summary>The instance of the current scene (main menu, game or map editor); read by the static patches.</summary>
        public static PfadfinderSettings Instance { get; private set; }

        public ModSetting<bool> AltEnablesRouting { get; } = new ModSetting<bool>(false,
            ModSettingDescriptor.Create("Hold Alt to route")
                .SetTooltip("Off: drags are routed, hold Alt for the vanilla L-shape.\nOn: drags are the vanilla L-shape, hold Alt to route."));

        public ModSetting<bool> RouteTubeways { get; } = new ModSetting<bool>(true,
            ModSettingDescriptor.Create("Route tubeways")
                .SetTooltip("Also route the Tubeway and Vertical Tubeway tools. Off: they always draw the vanilla L-shape."));

        public ModSetting<bool> RouteAcrossLevels { get; } = new ModSetting<bool>(true,
            ModSettingDescriptor.Create("Route across levels")
                .SetTooltip("When the drag ends on another height level, connect it through existing stairs (or vertical tubeways). No new stairs are built."));

        public ModSetting<bool> HideRangeWhileRouting { get; } = new ModSetting<bool>(false,
            ModSettingDescriptor.Create("Hide build range while routing")
                .SetTooltip("Don't draw the district's path range overlay while a routed path is being previewed."));

        public PfadfinderSettings(ISettings settings, ModSettingsOwnerRegistry modSettingsOwnerRegistry, ModRepository modRepository)
            : base(settings, modSettingsOwnerRegistry, modRepository)
        {
        }

        protected override string ModId => ModStarter.ModId;

        public override ModSettingsContext ChangeableOn => ModSettingsContext.All;

        protected override void OnAfterLoad()
        {
            Instance = this;
        }
    }
}
