using System.IO;
using ImageTray.Services;
using Xunit;

namespace ImageTray.Tests;

public class RotationTests
{
    private static readonly DateTime Base = new(2026, 7, 31, 9, 0, 0, DateTimeKind.Utc);

    private static ShotFile File(string name, int minutesOld) =>
        new($@"C:\shots\{name}", Base.AddMinutes(-minutesOld), Length: 1000 + minutesOld);

    [Fact]
    public void OrderNewestFirst_puts_the_most_recent_first()
    {
        var ordered = RotationPlanner.OrderNewestFirst([File("b.png", 10), File("a.png", 0), File("c.png", 5)]);

        Assert.Equal([@"C:\shots\a.png", @"C:\shots\c.png", @"C:\shots\b.png"], ordered.Select(f => f.Path));
    }

    [Fact]
    public void OrderNewestFirst_breaks_timestamp_ties_by_path_so_the_order_is_stable()
    {
        // A burst of captures, or a folder copy, easily lands several files on the
        // same timestamp. Without a tie-break the displayed order would wobble
        // between scans.
        var tied = new[]
        {
            new ShotFile(@"C:\shots\shot-01.png", Base, 10),
            new ShotFile(@"C:\shots\shot-03.png", Base, 10),
            new ShotFile(@"C:\shots\shot-02.png", Base, 10),
        };

        var first = RotationPlanner.OrderNewestFirst(tied).Select(f => f.Path).ToList();
        var again = RotationPlanner.OrderNewestFirst(tied.Reverse()).Select(f => f.Path).ToList();

        Assert.Equal([@"C:\shots\shot-03.png", @"C:\shots\shot-02.png", @"C:\shots\shot-01.png"], first);
        Assert.Equal(first, again);
    }

    [Fact]
    public void SelectForRemoval_returns_nothing_while_the_folder_is_under_the_limit()
    {
        var files = Enumerable.Range(0, 5).Select(i => File($"s{i}.png", i)).ToList();

        Assert.Empty(RotationPlanner.SelectForRemoval(files, keepCount: 5));
        Assert.Empty(RotationPlanner.SelectForRemoval(files, keepCount: 12));
    }

    [Fact]
    public void SelectForRemoval_keeps_exactly_the_newest_n()
    {
        // 0 minutes old is newest, 7 is oldest.
        var files = Enumerable.Range(0, 8).Select(i => File($"s{i}.png", i)).ToList();

        var doomed = RotationPlanner.SelectForRemoval(files, keepCount: 5);

        Assert.Equal(3, doomed.Count);
        Assert.Equal(
            [@"C:\shots\s7.png", @"C:\shots\s6.png", @"C:\shots\s5.png"],
            doomed.Select(f => f.Path));
    }

    [Fact]
    public void SelectForRemoval_hands_back_the_oldest_first()
    {
        // Oldest first matters because a rotation pass that only gets part way
        // through should have removed the least useful screenshots.
        var files = Enumerable.Range(0, 10).Select(i => File($"s{i}.png", i)).ToList();

        var doomed = RotationPlanner.SelectForRemoval(files, keepCount: 4);

        var ages = doomed.Select(f => f.LastWriteUtc).ToList();
        Assert.Equal(ages.OrderBy(t => t), ages);
    }

    [Fact]
    public void Rotate_recycles_the_files_past_the_limit_and_leaves_the_rest()
    {
        var files = new FakeShotFileSystem();
        files.Files.AddRange(Enumerable.Range(0, 6).Select(i => File($"s{i}.png", i)));

        var result = new RotationService(files).Rotate(files.Files.ToList(), keepCount: 4);

        // Six files, keep four, so exactly the two oldest go.
        Assert.Equal(2, result.Recycled);
        Assert.Equal(0, result.Failed);
        Assert.Equal([@"C:\shots\s5.png", @"C:\shots\s4.png"], files.Recycled);
    }

    [Fact]
    public void Rotate_counts_a_locked_file_as_failed_and_still_handles_the_others()
    {
        var files = new FakeShotFileSystem();
        files.Files.AddRange(Enumerable.Range(0, 8).Select(i => File($"s{i}.png", i)));
        files.Locked.Add(@"C:\shots\s7.png");

        var result = new RotationService(files).Rotate(files.Files.ToList(), keepCount: 5);

        Assert.Equal(1, result.Failed);
        Assert.Equal(2, result.Recycled);
        Assert.DoesNotContain(@"C:\shots\s7.png", files.Recycled);
        Assert.Contains(@"C:\shots\s6.png", files.Recycled);
        Assert.Contains(@"C:\shots\s5.png", files.Recycled);
    }

    [Fact]
    public void CountPendingRemoval_reports_without_removing_anything()
    {
        var files = new FakeShotFileSystem();
        files.Files.AddRange(Enumerable.Range(0, 30).Select(i => File($"s{i}.png", i)));

        var service = new RotationService(files);

        Assert.Equal(18, service.CountPendingRemoval(files.Files.ToList(), keepCount: 12));
        Assert.Empty(files.Recycled);
    }

    [Theory]
    [InlineData("shot.png", true)]
    [InlineData("shot.PNG", true)]
    [InlineData("shot.jpg", true)]
    [InlineData("shot.jpeg", true)]
    [InlineData("shot.bmp", true)]
    [InlineData("shot.gif", true)]
    [InlineData("notes.txt", false)]
    [InlineData("archive.zip", false)]
    // WebP only decodes when the optional Windows codec is installed, so it is
    // excluded rather than shown as a broken tile.
    [InlineData("shot.webp", false)]
    public void IsImageFile_matches_only_the_formats_the_app_can_decode(string name, bool expected) =>
        Assert.Equal(expected, WindowsShotFileSystem.IsImageFile($@"C:\shots\{name}"));

    private sealed class FakeShotFileSystem : IShotFileSystem
    {
        public List<ShotFile> Files { get; } = [];

        public List<string> Recycled { get; } = [];

        public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool DirectoryExists(string folder) => true;

        public IReadOnlyList<ShotFile> EnumerateImageFiles(string folder) => Files;

        public void RecycleFile(string path)
        {
            if (Locked.Contains(path))
            {
                throw new IOException($"'{path}' is in use.");
            }

            Recycled.Add(path);
            Files.RemoveAll(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        }
    }
}
