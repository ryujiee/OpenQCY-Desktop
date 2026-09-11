using OpenQCY_Desktop.Bluetooth;
using OpenQCY_Desktop.Device;
using OpenQCY_Desktop.Protocol;

namespace OpenQCY.Desktop.Tests;

[TestClass]
public sealed class QcyDeviceSafetyTests
{
    [TestMethod]
    [DataRow(19785)]
    [DataRow(19786)]
    [DataRow(0)]
    [DataRow(19790)]
    public async Task UnsupportedModelsAreRejectedBeforeAnyGattOperation(int vendorId)
    {
        await using var connection = new RecordingConnection(QcyModelProfile.FromVendorId((ushort)vendorId));
        await Assert.ThrowsAsync<NotSupportedException>(() => QcyDeviceClient.CreateAsync(connection));
        Assert.IsEmpty(connection.Operations);

        // This check executes before any Windows Bluetooth API is reached.
        var device = new BluetoothDeviceInfo("synthetic", "QCY MeloBuds N70", 0, null, null,
            (ushort)vendorId, -50, 0, 0, 0, false, false, false, DateTimeOffset.UnixEpoch);
        await Assert.ThrowsAsync<NotSupportedException>(() => new WindowsBluetoothTransport().ConnectAsync(device));
    }

    [TestMethod]
    public async Task Ht08GattTopologyDoesNotUnlockTheN70CommandSession()
    {
        // The physical HT08 exposes an A001 table that looks like the N70's:
        // 1001 (WriteWithoutResponse), 1002 (Read+Notify), 0007, 0008, 000B and
        // 000D are all present. Observed on hardware on 2026-09-11. A familiar
        // characteristic set is not evidence that the command protocol matches,
        // so the model gate must still refuse before touching any of them.
        await using var connection = new RecordingConnection(
            QcyModelProfile.FromVendorId(QcyModelProfile.Ht08VendorId),
            [
                new(QcyUuids.Command, false, true, false),
                new(QcyUuids.Notification, true, false, true),
                new(QcyUuids.FirmwareVersion, true, false, false),
                new(QcyUuids.Battery, true, false, true),
                new(QcyUuids.Equalizer, true, true, false),
                new(QcyUuids.KeyFunctions, true, true, false),
            ]);

        await Assert.ThrowsAsync<NotSupportedException>(() => QcyDeviceClient.CreateAsync(connection));

        // No subscription, no proprietary read, no query, no write.
        Assert.IsEmpty(connection.Operations);
    }

    [TestMethod]
    [DataRow(23872)]
    [DataRow(23877)]
    public async Task N70InitializationKeepsItsQueriesSubscriptionsAndDirectReads(int vendorId)
    {
        var connection = new RecordingConnection(QcyModelProfile.FromVendorId((ushort)vendorId));
        await using var client = await QcyDeviceClient.CreateAsync(connection);
        CollectionAssert.AreEqual(new[]
        {
            $"subscribe {QcyUuids.Notification}", $"subscribe {QcyUuids.Battery}",
            $"read {QcyUuids.Battery}", $"read {QcyUuids.FirmwareVersion}", $"read {QcyUuids.KeyFunctions}",
            "write FF03FE012C", "write FF03FE0117", "write FF03FE0109", "write FF03FE0110",
            "write FF03FE0123", "write FF03FE0124", "write FF03FE012A", "write FF03FE011D",
            "write FF03FE0114", "write FF03FE0122",
        }, connection.Operations.ToArray());
        Assert.AreEqual("L 3.0.13 · R 3.0.13", client.State.FirmwareVersion);
        Assert.AreEqual((byte)80, client.State.Battery.Left);
        Assert.AreEqual(QcyWearDetectionProtocol.WearingDetection, client.State.WearDetectionProtocol);

        connection.Operations.Clear();
        await client.RefreshBatteryAsync();
        CollectionAssert.AreEqual(new[] { $"read {QcyUuids.Battery}" }, connection.Operations.ToArray());

        await client.SetNoiseModeAsync(QcyNoiseMode.NoiseCancellation, QcyNoiseCancellationMode.Noisy);
        Assert.AreEqual("write FF051703010302", connection.Operations[^1]);
        Assert.AreEqual(QcyNoiseCancellationMode.Noisy, client.State.NoiseControl?.CancellationMode);
    }

    private sealed class RecordingConnection(
        QcyModelProfile profile,
        IReadOnlyCollection<GattCharacteristicInfo>? characteristics = null) : IBluetoothDeviceConnection
    {
        public QcyModelProfile ModelProfile => profile;
        public string Name => "Synthetic test device";
        public ulong BluetoothAddress => 0;
        public bool IsConnected => true;
        public List<string> Operations { get; } = [];
        public IReadOnlyCollection<GattCharacteristicInfo> Characteristics { get; } = characteristics ??
        [
            new(QcyUuids.Command, false, true, false), new(QcyUuids.Notification, false, false, true),
            new(QcyUuids.Battery, true, false, true), new(QcyUuids.FirmwareVersion, true, false, false),
            new(QcyUuids.KeyFunctions, true, true, false),
        ];

        public event EventHandler? Disconnected { add { } remove { } }
        public event EventHandler<GattValueChangedEventArgs>? ValueChanged;

        public Task<byte[]> ReadAsync(Guid uuid, CancellationToken cancellationToken = default)
        {
            Operations.Add($"read {uuid}");
            return Task.FromResult<byte[]>(uuid == QcyUuids.Battery ? [80, 70, 60] :
                uuid == QcyUuids.FirmwareVersion ? [3, 0, 13, 3, 0, 13] : [3, 1]);
        }

        public Task SubscribeAsync(Guid uuid, CancellationToken cancellationToken = default)
        {
            Operations.Add($"subscribe {uuid}");
            return Task.CompletedTask;
        }

        public Task WriteAsync(Guid uuid, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual(QcyUuids.Command, uuid);
            Operations.Add($"write {Convert.ToHexString(value.Span)}");
            var command = QcyPacket.Parse(value.Span).Single();
            var opcode = command.Opcode == 0xFE ? command.Parameters[0] : command.Opcode;
            byte[] parameters = command.Opcode != 0xFE ? command.Parameters : opcode switch
            {
                0x2C => [0, 1, 1], 0x17 => [1, 3, 2], 0x14 => [255, 255, 0, 0],
                0x1D => [4, 15], 0x22 => QcyCommands.BuildCustomEqualizer(new double[10])[4..],
                _ => [1],
            };
            ValueChanged?.Invoke(this, new(QcyUuids.Notification, QcyPacket.Pack(opcode, parameters)));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
