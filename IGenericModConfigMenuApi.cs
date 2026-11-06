using System;
using StardewModdingAPI;

namespace ChestItemFinder
{
    /// <summary>The subset of the Generic Mod Config Menu API this mod uses.</summary>
    public interface IGenericModConfigMenuApi
    {
        void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);

        void AddSectionTitle(IManifest mod, Func<string> text, Func<string>? tooltip = null);

        void AddKeybind(
            IManifest mod,
            Func<SButton> getValue,
            Action<SButton> setValue,
            Func<string> name,
            Func<string>? tooltip = null,
            string? fieldId = null);

        void AddNumberOption(
            IManifest mod,
            Func<int> getValue,
            Action<int> setValue,
            Func<string> name,
            Func<string>? tooltip = null,
            int? min = null,
            int? max = null,
            int? interval = null,
            Func<int, string>? formatValue = null,
            string? fieldId = null);
    }
}