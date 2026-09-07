using MCmodsLoader.Core.Models;

namespace MCmodsLoader.Core.Constants;

public static class ModPresets
{
    public static IReadOnlyList<ModDefinition> GetDefaultFpsModPack()
    {
        return new List<ModDefinition>
        {
            // Performance Mods
            new()
            {
                Slug = "sodium",
                Name = "Sodium",
                Description = "Next-generation graphics engine for massive FPS gains and smooth gameplay.",
                FabricModId = "sodium",
                Category = "Performance"
            },
            new()
            {
                Slug = "lithium",
                Name = "Lithium",
                Description = "Physics, AI, and chunk optimizations without gameplay changes.",
                FabricModId = "lithium",
                Category = "Performance"
            },
            new()
            {
                Slug = "ferrite-core",
                Name = "FerriteCore",
                Description = "Drastically reduces Minecraft's memory (RAM) usage.",
                FabricModId = "ferritecore",
                Category = "Performance"
            },
            new()
            {
                Slug = "entityculling",
                Name = "Entity Culling",
                Description = "Skips rendering hidden tiles and entities to significantly boost FPS.",
                FabricModId = "entityculling",
                Category = "Performance"
            },
            new()
            {
                Slug = "immediatelyfast",
                Name = "ImmediatelyFast",
                Description = "Optimizes immediate mode rendering to speed up HUD, fonts, and particle rendering.",
                FabricModId = "immediatelyfast",
                Category = "Performance"
            },

            // Quality of Life / HUD
            new()
            {
                Slug = "zoomify",
                Name = "Zoomify",
                Description = "Smooth, configurable zoom with mouse wheel (OptiFine style).",
                FabricModId = "zoomify",
                Category = "Quality of Life"
            },
            new()
            {
                Slug = "appleskin",
                Name = "AppleSkin",
                Description = "Displays food saturation and exhaustion preview directly in the HUD.",
                FabricModId = "appleskin",
                Category = "Quality of Life"
            },
            new()
            {
                Slug = "xaeros-minimap",
                Name = "Xaero's Minimap",
                Description = "Smooth in-game minimap with waypoints and entity radar.",
                FabricModId = "xaerominimap",
                Category = "Quality of Life"
            },
            new()
            {
                Slug = "xaeros-world-map",
                Name = "Xaero's World Map",
                Description = "Fullscreen world map displaying all explored areas and terrain.",
                FabricModId = "xaeroworldmap",
                Category = "Quality of Life"
            },
            new()
            {
                Slug = "modmenu",
                Name = "Mod Menu",
                Description = "Adds a clean in-game mod list and settings screen to the main menu.",
                FabricModId = "modmenu",
                Category = "Quality of Life"
            },
            new()
            {
                Slug = "lambdynamiclights",
                Name = "LambDynamicLights",
                Description = "Dynamic hand-held and entity lighting effects with high performance.",
                FabricModId = "lambdynlights",
                Category = "Quality of Life"
            },

            // Libraries / Dependencies
            new()
            {
                Slug = "fabric-api",
                Name = "Fabric API",
                Description = "Essential core library hook required by most Fabric mods.",
                FabricModId = "fabric-api",
                Category = "Library"
            },
            new()
            {
                Slug = "fabric-language-kotlin",
                Name = "Fabric Language Kotlin",
                Description = "Kotlin language runtime required by modern Kotlin-based mods.",
                FabricModId = "fabric-language-kotlin",
                Category = "Library"
            },
            new()
            {
                Slug = "yacl",
                Name = "YetAnotherConfigLib (YACL)",
                Description = "GUI and configuration library required by Zoomify and others.",
                FabricModId = "yet_another_config_lib_v3",
                Category = "Library"
            },
            new()
            {
                Slug = "placeholder-api",
                Name = "Text Placeholder API",
                Description = "Text formatting and placeholder library for HUD elements.",
                FabricModId = "placeholder-api",
                Category = "Library"
            }
        };
    }
}
