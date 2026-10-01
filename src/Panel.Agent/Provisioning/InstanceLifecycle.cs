using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.Fleet.Infrastructure;
using Panel.Fleet.Ports;
using Panel.SharedKernel;

namespace Panel.Agent.Provisioning;

/// <summary>Установка службы и удаление экземпляров, созданных панелью.</summary>
internal sealed class InstanceLifecycle(ProvisioningConfiguration settings, IRegistryStore registry, RegistryEditor editor, IProvisioningPlatform platform, GamePorts ports)
{
	/// <summary>
	/// Юнит из шаблона, <c>daemon-reload</c>, <c>enable</c>. Только для сервера, пути которого
	/// вывел демон.
	/// </summary>
	/// <remarks>
	/// Кроме имени юнита и транспорта сверяются каталоги программы, данных и FIFO. Шаблон
	/// заполняется выведенными значениями, и при расхождении с реестром юнит получился бы для
	/// другого сервера.
	/// </remarks>
	public async Task<Result<ServiceInstalledPayload>> InstallServiceAsync(InstanceId instance, CancellationToken ct)
	{
		var configuration = await settings.ReadAsync(ct).ConfigureAwait(false);
		if (!configuration.IsSuccess)
		{
			return configuration.Failure!;
		}

		var snapshot = await registry.LoadAsync(ct).ConfigureAwait(false);
		if ((snapshot as RegistrySnapshot.Available)?.Find(instance) is not { } server)
		{
			return Failure.NotFound($"Unknown instance {instance.Value}");
		}

		var layout = settings.CreatedLayout(configuration.Value, server);
		if (!layout.IsSuccess)
		{
			return layout.Failure!;
		}

		var paths = layout.Value;
		if (server.Unit != paths.Unit
			|| server.ProgramDirectory != paths.ProgramDirectory
			|| server.DataDirectory != paths.DataDirectory
			|| server.ConsoleFifo != paths.ConsoleFifo)
		{
			return Failure.Conflict("This instance was not created by the panel and its unit is managed elsewhere");
		}

		if (server.CommandTransport != paths.CommandTransport)
		{
			return Failure.Conflict(paths.Layout == PlatformLayout.Windows
				? "Only the supervised transport is available on Windows"
				: "Only the fifo transport has a generated unit");
		}

		var installed = await platform.InstallServiceAsync(paths, server, ct).ConfigureAwait(false);
		if (!installed.IsSuccess)
		{
			return installed;
		}

		// Игрокам нужен порт, записанный в настройки сервера; реестр — если настройки не читаются.
		var port = await GamePorts.ConfiguredPortAsync(server, ct).ConfigureAwait(false);
		var warning = await ports.OpenAsync(port > 0 ? port : GamePorts.PortOf(server), ct).ConfigureAwait(false);
		return installed.Value with { FirewallWarning = warning };
	}

	/// <summary>
	/// Остановить и снять юнит, стереть дерево, убрать сервер из реестра. Только для сервера, пути
	/// которого вывел демон.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Сервер, заведённый вручную до панели, указывает на каталоги, которыми панель не владеет;
	/// такие серверы не удаляются.
	/// </para>
	/// <para>
	/// Если дерево стёрто не полностью, отказ перечисляет остаток, а запись реестра остаётся,
	/// чтобы оператор мог повторить удаление.
	/// </para>
	/// <para>
	/// Реестр только для чтения (версия 1) проверяется до остановки юнита, а не после удаления
	/// файлов.
	/// </para>
	/// </remarks>
	public async Task<Result<InstanceDeletedPayload>> DeleteInstanceAsync(InstanceId instance, CancellationToken ct)
	{
		var configuration = await settings.ReadAsync(ct).ConfigureAwait(false);
		if (!configuration.IsSuccess)
		{
			return configuration.Failure!;
		}

		var snapshot = await registry.LoadAsync(ct).ConfigureAwait(false);
		if (snapshot is not RegistrySnapshot.Available registered)
		{
			return Failure.Unavailable(((RegistrySnapshot.Unavailable) snapshot).Detail);
		}

		if (registered.Find(instance) is not { } server)
		{
			return Failure.NotFound($"Unknown instance {instance.Value}");
		}

		if (registered.Servers.Count == 1)
		{
			return Failure.Conflict("Cannot delete the only instance; create another server first");
		}

		if (configuration.Value.IsReadOnlyRegistry)
		{
			return Failure.Conflict(
				"The registry is read-only while /etc/vs-panel.json is version 1; run scripts/install.sh to migrate");
		}

		var layout = settings.CreatedLayout(configuration.Value, server);
		if (!layout.IsSuccess)
		{
			return layout.Failure!;
		}

		var paths = layout.Value;
		if (server.Unit != paths.Unit
			|| server.DataDirectory != paths.DataDirectory
			|| server.ProgramDirectory != paths.ProgramDirectory)
		{
			return Failure.Conflict("This instance was not created by the panel; its files are managed elsewhere");
		}

		// Порт читается до того, как дерево сервера вместе с настройками будет стёрто.
		var port = await GamePorts.ConfiguredPortAsync(server, ct).ConfigureAwait(false);
		var removed = await platform.RemoveServiceAsync(paths, server, ct).ConfigureAwait(false);
		if (!removed.IsSuccess)
		{
			return removed.Failure!;
		}

		try
		{
			Directory.Delete(paths.Root, recursive: true);
		}
		catch (DirectoryNotFoundException)
		{
			// Дерева уже нет.
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Service removed but {paths.Root} could not be erased: {exception.Message}");
		}

		var written = await editor.EditAsync(document =>
		{
			var remaining = document.Registered;
			if (!remaining.Contains(instance))
			{
				// Запись уже удалил кто-то другой.
				return Result.Ok(document.Default ?? instance);
			}

			var next = RegistryRules.DefaultAfterRemoving(remaining, document.Default, instance);
			document.Instances.Remove(instance.Value);
			document.Default = next;
			return Result.Ok(next);
		}, ct).ConfigureAwait(false);
		if (!written.IsSuccess)
		{
			return written.Failure!;
		}

		// Сервера больше нет, и правило его порта не нужно. Порт другого сервера не снимается.
		await ports.CloseAsync(instance, port > 0 ? port : GamePorts.PortOf(server), ct).ConfigureAwait(false);
		return new InstanceDeletedPayload
		{
			Id = instance.Value,
			Root = paths.Root,
			Unit = server.Unit,
			Default = written.Value.Value,
		};
	}
}
