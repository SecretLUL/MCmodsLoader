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
                FabricModId = "sodium"
            },
            new()
            {
                Slug = "lithium",
                Name = "Lithium",
                FabricModId = "lithium"
            },
            new()
            {
                Slug = "ferrite-core",
                Name = "FerriteCore",
                FabricModId = "ferritecore"
            },
            new()
            {
                Slug = "entityculling",
                Name = "Entity Culling",
                FabricModId = "entityculling"
            },
            new()
            {
                Slug = "immediatelyfast",
                Name = "ImmediatelyFast",
                FabricModId = "immediatelyfast"
            },

            // Quality of Life / HUD
            new()
            {
                Slug = "zoomify",
                Name = "Zoomify",
                FabricModId = "zoomify"
            },
            new()
            {
                Slug = "appleskin",
                Name = "AppleSkin",
                FabricModId = "appleskin"
            },
            new()
            {
                Slug = "xaeros-minimap",
                Name = "Xaero's Minimap",
                FabricModId = "xaerominimap"
            },
            new()
            {
                Slug = "xaeros-world-map",
                Name = "Xaero's World Map",
                FabricModId = "xaeroworldmap"
            },
            new()
            {
                Slug = "modmenu",
                Name = "Mod Menu",
                FabricModId = "modmenu"
            },
            new()
            {
                Slug = "lambdynamiclights",
                Name = "LambDynamicLights",
                FabricModId = "lambdynlights"
            },

            // Libraries / Dependencies
            new()
            {
                Slug = "fabric-api",
                Name = "Fabric API",
                FabricModId = "fabric-api"
            },
            new()
            {
                Slug = "fabric-language-kotlin",
                Name = "Fabric Language Kotlin",
                FabricModId = "fabric-language-kotlin"
            },
            new()
            {
                Slug = "yacl",
                Name = "YetAnotherConfigLib (YACL)",
                FabricModId = "yet_another_config_lib_v3"
            },
            new()
            {
                Slug = "placeholder-api",
                Name = "Text Placeholder API",
                FabricModId = "placeholder-api"
            }
        };
    }
}
