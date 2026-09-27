using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Panel.Web.Endpoints;

namespace Panel.Web.Tests;

/// <summary>
/// Поток событий не держит остановку панели: иначе открытая вкладка браузера задерживала бы
/// остановку службы до <c>ShutdownTimeout</c>, и установщик не мог бы обновить панель.
/// </summary>
public sealed class ServerSentEventsTests
{
	[Fact]
	public void Остановка_панели_обрывает_поток()
	{
		using var stopping = new CancellationTokenSource();
		var lifetime = new Lifetime(stopping.Token);
		var aborts = new AbortFeature();
		var http = new DefaultHttpContext
		{
			RequestServices = new ServiceCollection().AddSingleton<IHostApplicationLifetime>(lifetime).BuildServiceProvider(),
		};
		http.Features.Set<IHttpRequestLifetimeFeature>(aborts);

		ServerSentEvents.Begin(http);
		Assert.False(aborts.Aborted);

		stopping.Cancel();

		Assert.True(aborts.Aborted);
		Assert.Equal("text/event-stream", http.Response.ContentType);
	}

	private sealed class AbortFeature : IHttpRequestLifetimeFeature
	{
		public bool Aborted { get; private set; }

		public CancellationToken RequestAborted { get; set; }

		public void Abort() => Aborted = true;
	}

	private sealed class Lifetime(CancellationToken stopping) : IHostApplicationLifetime
	{
		public CancellationToken ApplicationStarted => CancellationToken.None;

		public CancellationToken ApplicationStopping => stopping;

		public CancellationToken ApplicationStopped => CancellationToken.None;

		public void StopApplication()
		{
		}
	}
}
