using System.Text.Json;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Protocol;
using InputExecutor.Core.Recording;

namespace InputExecutor.Tests;

public sealed class JsonLinesTrajectorySinkTests
{
    [Fact]
    public async Task BatchedWrite_PersistsEverySequenceWithoutGaps()
    {
        const int recordCount = 20_000;
        var path = Path.Combine(Path.GetTempPath(), $"inputd-jsonl-batch-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var sink = new JsonLinesTrajectorySink(path))
            {
                for (var offset = 0; offset < recordCount; offset += 1_000)
                {
                    var batch = Enumerable.Range(offset + 1, Math.Min(1_000, recordCount - offset))
                        .Select(sequence => new TrajectoryRecord
                        {
                            RecordedAtUtc = DateTimeOffset.UnixEpoch,
                            SessionId = "batch-test",
                            Source = "test",
                            Sequence = sequence,
                            ActionRequested = new HumanInputFrame
                            {
                                Device = "mouse",
                                TimestampUs = sequence,
                                Mouse = new MouseFrame { Dx = 1 }
                            }
                        })
                        .ToArray();
                    await sink.WriteBatchAsync(batch, default);
                }
            }

            var expectedSequence = 1L;
            foreach (var line in File.ReadLines(path))
            {
                using var json = JsonDocument.Parse(line);
                Assert.Equal(expectedSequence++, json.RootElement.GetProperty("sequence").GetInt64());
            }
            Assert.Equal(recordCount + 1L, expectedSequence);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
