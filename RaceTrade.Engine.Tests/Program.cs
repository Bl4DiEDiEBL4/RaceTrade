using System;
using System.Collections.Generic;

internal static class Program
{
    private static int Main()
    {
        try
        {
            LanguageCodeIsOutputOnly();
            XxxLanguageReleaseContinuesIntoXxxGroup();
            Console.WriteLine("All RaceTrade.Engine regression tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void LanguageCodeIsOutputOnly()
    {
        var config = new ReleaseClassifierConfig
        {
            LanguageMappings = new List<ReleaseLanguageMapping>
            {
                new() { Code = "NO", Aliases = new List<string> { "NORWEGiAN", "NORWEGIAN", "NORWAY", "-NO-" } }
            },
            Rules = new List<ReleaseClassifierRule>
            {
                new() { FromSection = "GENERAL", Match = "[language] exists", SetSection = "EBOOK-{lang}" }
            }
        };

        var magazine = ReleaseClassifier.Classify(
            "WellBeing.No.224.2026.RETAIL.MAGAZINE.eBook-21A1",
            "GENERAL",
            SectionDetectionModes.ClassifierOnly,
            config);
        Equal("GENERAL", magazine.FinalSection, "Language code NO must not match .No. when NO is not an alias.");

        var music = ReleaseClassifier.Classify(
            "Horizon_Ablaze-Livets_Host-WEB-NO-2026-ENTiTLED",
            "GENERAL",
            SectionDetectionModes.ClassifierOnly,
            config);
        Equal("EBOOK-NO", music.FinalSection, "Alias -NO- should match WEB-NO- and output code NO.");
    }

    private static void XxxLanguageReleaseContinuesIntoXxxGroup()
    {
        var config = new ReleaseClassifierConfig
        {
            LanguageMappings = new List<ReleaseLanguageMapping>
            {
                new() { Code = "FR", Aliases = new List<string> { "FRENCH" } },
                new() { Code = "DE", Aliases = new List<string> { "GERMAN" } }
            },
            Rules = new List<ReleaseClassifierRule>
            {
                new()
                {
                    FromSection = "GENERAL",
                    Match = "[release] regex (^|[-._])XXX($|[-._])",
                    SetSection = "XXX",
                    Continue = true
                },
                new()
                {
                    FromSection = "XXX",
                    Match = "[language] exists",
                    SetSection = "XXX-PAYSITE-{lang}"
                },
                new()
                {
                    FromSection = "XXX",
                    Match = "[release] regex (^|[-._])MP4($|[-._])",
                    SetSection = "XXX-PAYSITE"
                }
            }
        };

        var plain = ReleaseClassifier.Classify(
            "ScandalousGFs.Aurora.Spy.Cam.Watches.Aurora.Shower.XXX.1080p.MP4-LEWD",
            "GENERAL",
            SectionDetectionModes.ClassifierOnly,
            config);
        Equal("XXX-PAYSITE", plain.FinalSection, "Plain XXX MP4 should route to XXX-PAYSITE.");
        Equal(2, plain.Steps.Count, "Plain XXX MP4 should have GENERAL -> XXX -> XXX-PAYSITE steps.");

        var french = ReleaseClassifier.Classify(
            "ScandalousGFs.Aurora.Spy.Cam.Watches.Aurora.Shower.FRENCH.XXX.1080p.MP4-LEWD",
            "GENERAL",
            SectionDetectionModes.ClassifierOnly,
            config);
        Equal("XXX-PAYSITE-FR", french.FinalSection, "Language-tagged XXX MP4 should continue into the XXX group and apply {lang}.");
        Equal(2, french.Steps.Count, "Language-tagged XXX MP4 should have GENERAL -> XXX -> XXX-PAYSITE-FR steps.");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
    }
}