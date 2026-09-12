using System.Collections;
using System.IO;
using GameSaveManager.Contracts;
using Moq;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace GameSaveManager.Playnite.Tests;

public sealed class CompanionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "GameSaveManager.Playnite.Tests", Guid.NewGuid().ToString("N"));
    private readonly List<Game> games = new();
    private readonly Mock<IPlayniteAPI> api = new();
    public CompanionTests()
    {
        Directory.CreateDirectory(root);
        var collection = new Mock<IItemCollection<Game>>();
        collection.As<IEnumerable>().Setup(x => x.GetEnumerator()).Returns(() => games.GetEnumerator());
        collection.As<IEnumerable<Game>>().Setup(x => x.GetEnumerator()).Returns(() => games.GetEnumerator());
        collection.As<ICollection<Game>>().Setup(x => x.Add(It.IsAny<Game>())).Callback<Game>(games.Add);
        collection.Setup(x => x.Add(It.IsAny<IEnumerable<Game>>())).Callback<IEnumerable<Game>>(items => games.AddRange(items));
        collection.Setup(x => x.Get(It.IsAny<Guid>())).Returns<Guid>(id => games.FirstOrDefault(x => x.Id == id));
        var database = new Mock<IGameDatabaseAPI>(); database.SetupGet(x => x.Games).Returns(collection.Object);
        var paths = new Mock<IPlaynitePathsAPI>(); paths.SetupGet(x => x.ExtensionsDataPath).Returns(root);
        api.SetupGet(x => x.Database).Returns(database.Object); api.SetupGet(x => x.Paths).Returns(paths.Object);
        api.Setup(x => x.ExpandGameVariables(It.IsAny<Game>(), It.IsAny<string>())).Returns<Game, string>((game, path) => path.Replace("{InstallDir}", game.InstallDirectory));
    }
    private LibraryGame Incoming()
    {
        var directory = Path.Combine(root, "Game"); Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "game.exe"); File.WriteAllText(executable, "fixture");
        return new LibraryGame { SourceId = "stable-id", Name = "Fixture Game", InstallDirectory = directory, Executable = executable };
    }
    [Fact]
    public void RetriedImportsAndExtensionRestartsDoNotDuplicateGames()
    {
        var incoming = Incoming(); var plugin = new Companion(api.Object);
        var first = plugin.Import("one", incoming); var second = plugin.Import("two", incoming);
        var restarted = new Companion(api.Object).Import("three", incoming);
        Assert.Single(games); Assert.Equal(first.Id, second.Id); Assert.Equal(first.Id, restarted.Id);
    }
    [Fact]
    public void ExistingLibraryMetadataAndLaunchArgumentsArePreserved()
    {
        var incoming = Incoming(); games.Add(new Game { Name = "My custom title", InstallDirectory = incoming.InstallDirectory, Description = "My notes", GameActions = new() { new() { Type = GameActionType.File, IsPlayAction = true, Path = incoming.Executable, Arguments = "--custom" } } });
        new Companion(api.Object).Import("request", incoming);
        var game = Assert.Single(games); Assert.Equal("My custom title", game.Name); Assert.Equal("My notes", game.Description); Assert.Equal("--custom", game.GameActions[0].Arguments);
    }
    [Fact]
    public void RemovedImportedGameIsNotSilentlyRecreated()
    {
        var incoming = Incoming(); var plugin = new Companion(api.Object); plugin.Import("first", incoming); games.Clear();
        Assert.Throws<InvalidOperationException>(() => new Companion(api.Object).Import("retry", incoming)); Assert.Empty(games);
    }
    [Fact]
    public void RemapChecksEveryDestinationBeforeChangingLibrary()
    {
        var incoming = Incoming(); var plugin = new Companion(api.Object); var imported = plugin.Import("first", incoming);
        Assert.Throws<InvalidOperationException>(() => plugin.Remap(new() { new() { Id = imported.Id, InstallDirectory = Path.Combine(root, "missing"), Executable = "missing.exe" } }));
        Assert.Equal(incoming.InstallDirectory, games[0].InstallDirectory);
        var destination = Path.Combine(root, "new-pc"); Directory.CreateDirectory(destination); var executable = Path.Combine(destination, "game.exe"); File.WriteAllText(executable, "fixture");
        plugin.Remap(new() { new() { Id = imported.Id, InstallDirectory = destination, Executable = executable } });
        Assert.Equal(destination, games[0].InstallDirectory); Assert.Equal(executable, games[0].GameActions[0].Path);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
