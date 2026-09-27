using System.Security.Cryptography;
using Panel.Contracts.Agent;
using Panel.SharedKernel;
using Panel.Web.Application;

namespace Panel.Web.Tests;

public sealed class TransferUploadTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-transfer-tests-" + Guid.NewGuid().ToString("n"));

	[Fact]
	public async Task StreamIsHashedAndBoundedWithoutContentLength()
	{
		var bytes = "transfer archive"u8.ToArray();
		var path = Path.Combine(directory, "transfer.tar.gz");
		var result = await TransferUpload.WriteAsync(new MemoryStream(bytes), path, bytes.Length, CancellationToken.None);
		Assert.True(result.IsSuccess);
		Assert.Equal(bytes.Length, result.Value.Length);
		Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), result.Value.Sha256);
		Assert.Equal(bytes, await File.ReadAllBytesAsync(path));

		var excess = await TransferUpload.WriteAsync(new MemoryStream(bytes), Path.Combine(directory, "excess.tar.gz"), bytes.Length - 1, CancellationToken.None);
		Assert.False(excess.IsSuccess);
		Assert.Equal(FailureCode.TooLarge, excess.Failure!.Code);
	}

	[Fact]
	public void PlanCannotBeUsedFromOtherSessionOrReused()
	{
		var clock = TimeProvider.System;
		var plans = new TransferPlans(new StagingWriter(new PanelOptions { StateDirectory = directory }), clock);
		var transfer = StagedFile.CreateFresh(StagedFile.Purpose.Transfer, ".tar.gz", "1234");
		var plan = new TransferPlan(transfer, new string('a', 64), 12, "data/Saves/main.vcdbs", new TransferInspectionPayload { ActiveWorld = "data/Saves/main.vcdbs" });
		var token = plans.Add(plan, "session-a");
		Assert.False(plans.Take(token, "session-b").IsSuccess);
		Assert.Equal(plan, plans.Take(token, "session-a").Value);
		Assert.False(plans.Take(token, "session-a").IsSuccess);
	}

	public void Dispose()
	{
		if (Directory.Exists(directory)) Directory.Delete(directory, true);
	}
}
