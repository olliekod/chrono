using System;
using System.Collections.Generic;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class GameClassifierTests
    {
        private static ProcessFacts Facts(string name, string? path = null, bool window = true, bool covers = false, bool gfx = false, bool known = false)
            => new(name, path, window, covers, gfx, known);

        // ------------------------------------------------------------ real games are found

        [Fact]
        public void ASteamGame_IsAGame_EvenWindowedAndUnknownToWindows()
        {
            var v = GameClassifier.Classify(Facts("Risk of Rain 2.exe", @"C:\Program Files (x86)\Steam\steamapps\common\Risk of Rain 2\Risk of Rain 2.exe"));
            Assert.True(v.IsGame);
        }

        [Theory]
        [InlineData(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe")]
        [InlineData(@"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe")]
        [InlineData(@"D:\SteamLibrary\steamapps\common\Deadlock\game\bin\win64\project8.exe")]
        [InlineData(@"C:\XboxGames\Forza Horizon 5\Content\ForzaHorizon5.exe")]
        [InlineData(@"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\XDefiant\XDefiant.exe")]
        [InlineData(@"C:\GOG Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe")]
        public void GamesInStoreLibraries_AreGames(string path)
        {
            Assert.True(GameClassifier.Classify(Facts(System.IO.Path.GetFileName(path), path)).IsGame);
        }

        [Fact]
        public void AProgramWindowsAlreadyKnowsAsAGame_IsAGame_Anywhere()
        {
            // Dolphin lives on the desktop, but Windows has it on its game list.
            var v = GameClassifier.Classify(Facts("Dolphin.exe", @"C:\Users\Player\Desktop\WiiHB\Dolphin-x64\Dolphin.exe", known: true));
            Assert.True(v.IsGame);
            Assert.Contains("Windows", v.Reason);
        }

        [Fact]
        public void AFullscreenProgramWithA3DApi_IsAGame_EvenOutsideLibraries()
        {
            Assert.True(GameClassifier.Classify(Facts("MyIndieGame.exe", @"C:\Users\Player\Downloads\MyIndieGame\MyIndieGame.exe", covers: true, gfx: true)).IsGame);
        }

        [Fact]
        public void AnUnreadablePath_DoesNotStopFullscreenDetection()
        {
            Assert.True(GameClassifier.Classify(Facts("game.exe", path: null, covers: true, gfx: true)).IsGame);
        }

        // ------------------------------------------------------- nothing else is picked

        [Theory]
        [InlineData("chrome.exe")]
        [InlineData("msedge.exe")]
        [InlineData("firefox.exe")]
        [InlineData("Discord.exe")]
        [InlineData("Spotify.exe")]
        [InlineData("vlc.exe")]
        [InlineData("Video.UI.exe")]
        [InlineData("obs64.exe")]
        [InlineData("Teams.exe")]
        [InlineData("explorer.exe")]
        [InlineData("Code.exe")]
        public void BrowsersChatMediaAndToolsAreNeverGames_EvenFullscreenWith3DApis(string exe)
        {
            // Watching a film fullscreen in a browser or player looks exactly like a game to the "fullscreen" rule.
            Assert.False(GameClassifier.Classify(Facts(exe, covers: true, gfx: true, known: true)).IsGame);
        }

        [Fact]
        public void TheChronoWindowItself_IsNeverAGame()
        {
            Assert.False(GameClassifier.Classify(Facts("ChronoRecorder.exe", covers: true, gfx: true)).IsGame);
        }

        [Fact]
        public void StoreClientsAreNotGames_EvenThoughTheirFoldersLookLikeGameFolders()
        {
            Assert.False(GameClassifier.Classify(Facts("steamwebhelper.exe", @"C:\Program Files (x86)\Steam\bin\cef\cef.win64\steamwebhelper.exe")).IsGame);
            Assert.False(GameClassifier.Classify(Facts("Steam.exe", @"C:\Program Files (x86)\Steam\steam.exe")).IsGame);
            Assert.False(GameClassifier.Classify(Facts("EpicGamesLauncher.exe", @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win32\EpicGamesLauncher.exe")).IsGame);
        }

        [Theory]
        [InlineData("CrashHandler.exe")]
        [InlineData("UnityCrashHandler64.exe")]
        [InlineData("GameLauncher.exe")]
        [InlineData("vc_redist.x64.exe")]
        [InlineData("setup.exe")]
        public void HelpersInsideAGameFolder_AreNotTheGame(string exe)
        {
            string path = @"C:\Program Files (x86)\Steam\steamapps\common\Some Game\" + exe;
            Assert.False(GameClassifier.Classify(Facts(exe, path)).IsGame);
        }

        [Fact]
        public void WithoutAWindow_NothingIsAGame()
        {
            Assert.False(GameClassifier.Classify(Facts("game.exe", @"C:\Program Files (x86)\Steam\steamapps\common\G\game.exe", window: false, known: true)).IsGame);
        }

        [Fact]
        public void AWindowedProgramWithNoSignals_IsNotAGame()
        {
            var v = GameClassifier.Classify(Facts("SomeTool.exe", @"C:\Tools\SomeTool.exe", gfx: true));   // 3D API but not fullscreen
            Assert.False(v.IsGame);
        }

        [Fact]
        public void FullscreenWithoutAGraphicsApi_IsNotAGame()
        {
            Assert.False(GameClassifier.Classify(Facts("Presentation.exe", @"C:\Tools\Presentation.exe", covers: true)).IsGame);
        }

        // ------------------------------------------------------------ choosing between games

        private static GameCandidate G(int pid, string name, int minutesAgo, bool foreground = false)
            => new(pid, name, name, new DateTime(2026, 9, 20, 20, 0, 0, DateTimeKind.Utc).AddMinutes(-minutesAgo), foreground);

        [Fact]
        public void NoGames_NothingChosen()
        {
            Assert.Null(GameClassifier.Choose(new List<GameCandidate>(), null));
        }

        [Fact]
        public void TheGameInFront_Wins()
        {
            var chosen = GameClassifier.Choose(new[] { G(1, "Old", 60), G(2, "Front", 90, foreground: true), G(3, "New", 1) }, null);
            Assert.Equal(2, chosen!.Pid);
        }

        [Fact]
        public void WithNoneInFront_TheNewestLaunchedWins()
        {
            var chosen = GameClassifier.Choose(new[] { G(1, "Old", 60), G(3, "New", 1), G(2, "Mid", 30) }, null);
            Assert.Equal(3, chosen!.Pid);
        }

        [Fact]
        public void TheCurrentGame_IsKeptWhileItRuns_EvenWhenAnotherComesToTheFront()
        {
            // Recording shouldn't hop between games every time you tab.
            var chosen = GameClassifier.Choose(new[] { G(1, "Current", 60), G(2, "Front", 5, foreground: true) }, currentPid: 1);
            Assert.Equal(1, chosen!.Pid);
        }

        [Fact]
        public void WhenTheCurrentGameHasClosed_ANewOneIsChosen()
        {
            var chosen = GameClassifier.Choose(new[] { G(2, "Other", 5) }, currentPid: 1);
            Assert.Equal(2, chosen!.Pid);
        }
    }
}
