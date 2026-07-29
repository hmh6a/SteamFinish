using SteamFinish.Core.Steam;
using SteamFinish.Core.Vdf;

namespace SteamFinish.Tests;

public class VdfParserTests
{
    [Fact]
    public void ParsesNestedObjectsAndValues()
    {
        var document = VdfParser.Parse("""
            "AppState"
            {
            	"appid"		"570"
            	"name"		"Dota 2"
            	"UserConfig"
            	{
            		"language"		"english"
            	}
            }
            """);

        var state = document.Unwrap("AppState");
        Assert.Equal("570", state.GetString("appid"));
        Assert.Equal("Dota 2", state.GetString("name"));
        Assert.Equal("english", state["UserConfig"]!.GetString("language"));
    }

    [Fact]
    public void KeyLookupIgnoresCase()
    {
        var state = VdfParser.Parse("""
            "AppState" { "StateFlags" "4" }
            """).Unwrap();

        Assert.Equal(4, state.GetInt64("stateflags"));
    }

    [Fact]
    public void UnescapesBackslashesAndQuotes()
    {
        var root = VdfParser.Parse("""
            "libraryfolders" { "path" "D:\\Games\\Steam" "label" "the \"main\" one" }
            """).Unwrap();

        Assert.Equal(@"D:\Games\Steam", root.GetString("path"));
        Assert.Equal("the \"main\" one", root.GetString("label"));
    }

    [Fact]
    public void SkipsCommentsAndPlatformConditionals()
    {
        var root = VdfParser.Parse("""
            // leading comment
            "root"
            {
            	"a"		"1" [$WIN32]
            	// another comment
            	"b"		"2"
            }
            """).Unwrap();

        Assert.Equal(1, root.GetInt64("a"));
        Assert.Equal(2, root.GetInt64("b"));
    }

    [Fact]
    public void LastDefinitionOfADuplicateKeyWins()
    {
        var root = VdfParser.Parse("""
            "root" { "value" "1" "value" "2" }
            """).Unwrap();

        Assert.Equal("2", root.GetString("value"));
    }

    [Fact]
    public void GetInt64FallsBackWhenTheValueIsNotANumber()
    {
        var root = VdfParser.Parse("""
            "root" { "value" "not a number" }
            """).Unwrap();

        Assert.Equal(-1, root.GetInt64("value", -1));
        Assert.Equal(-1, root.GetInt64("missing", -1));
    }

    [Fact]
    public void ReadsModernLibraryFoldersLayout()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Root, "libraryfolders.vdf");
        File.WriteAllText(path, """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"C:\\Program Files (x86)\\Steam"
            		"apps" { "570" "1234" }
            	}
            	"1"
            	{
            		"path"		"D:\\SteamLibrary"
            	}
            }
            """);

        Assert.Equal(
            [@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"],
            SteamLocator.ReadLibraryFolders(path));
    }

    [Fact]
    public void ReadsLegacyLibraryFoldersLayout()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Root, "libraryfolders.vdf");
        File.WriteAllText(path, """
            "LibraryFolders"
            {
            	"TimeNextStatsReport"		"1234567890"
            	"ContentStatsID"		"-1"
            	"1"		"D:\\SteamLibrary"
            	"2"		"E:\\Games"
            }
            """);

        Assert.Equal([@"D:\SteamLibrary", @"E:\Games"], SteamLocator.ReadLibraryFolders(path));
    }

    [Fact]
    public void MissingLibraryFileYieldsNoPaths()
    {
        Assert.Empty(SteamLocator.ReadLibraryFolders(Path.Combine(Path.GetTempPath(), "does-not-exist.vdf")));
    }
}
