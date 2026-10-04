// SPIKE: can this machine act as a GATT central against its own GATT server?
// One adapter usually refuses self-connection, but if it works we get a real
// end-to-end BLE round trip without a phone.
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

internal static class LoopbackProbe
{
    private static readonly Guid ServiceUuid = new("7a1c9e40-2f3d-4b6c-9a11-6c5d3e8f2b01");
    private static readonly Guid CharAUuid = new("7a1c9e41-2f3d-4b6c-9a11-6c5d3e8f2b01");
    private static readonly Guid CharBUuid = new("7a1c9e42-2f3d-4b6c-9a11-6c5d3e8f2b01");

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== LOOPBACK PROBE (self-connect as GATT central) ===");

        BluetoothAdapter? adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null)
        {
            Console.WriteLine("[fail] no adapter");
            return 1;
        }
        ulong address = adapter.BluetoothAddress;
        Console.WriteLine($"[info] own address = {address:X12}");

        // --- server side -----------------------------------------------------
        GattServiceProviderResult created = await GattServiceProvider.CreateAsync(ServiceUuid);
        Console.WriteLine($"[srv ] CreateAsync -> {created.Error}");
        if (created.ServiceProvider is null) return 1;
        GattServiceProvider provider = created.ServiceProvider;

        byte[] challenge = new byte[32];
        for (int i = 0; i < challenge.Length; i++) challenge[i] = (byte)(0xA0 + i);
        byte[] received = Array.Empty<byte>();
        bool notified = false;

        GattLocalCharacteristicResult ca = await provider.Service.CreateCharacteristicAsync(
            CharAUuid,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
                ReadProtectionLevel = GattProtectionLevel.Plain,
                WriteProtectionLevel = GattProtectionLevel.Plain,
            });
        GattLocalCharacteristicResult cb = await provider.Service.CreateCharacteristicAsync(
            CharBUuid,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Write,
                ReadProtectionLevel = GattProtectionLevel.Plain,
                WriteProtectionLevel = GattProtectionLevel.Plain,
            });
        if (ca.Characteristic is null || cb.Characteristic is null)
        {
            Console.WriteLine($"[fail] characteristic creation: A={ca.Error} B={cb.Error}");
            return 1;
        }

        ca.Characteristic.ReadRequested += (s, e) =>
        {
            GattReadRequest req = e.GetRequestAsync().AsTask().GetAwaiter().GetResult();
            req.RespondWithValue(CryptographicBuffer.CreateFromByteArray(challenge));
            Console.WriteLine($"[srv ] read served offset={req.Offset} length={req.Length}");
        };
        ca.Characteristic.SubscribedClientsChanged += (s, e) =>
            Console.WriteLine($"[srv ] SubscribedClients = {s.SubscribedClients.Count}");
        cb.Characteristic.WriteRequested += (s, e) =>
        {
            GattWriteRequest req = e.GetRequestAsync().AsTask().GetAwaiter().GetResult();
            var buf = new byte[req.Value.Length];
            using (var dr = DataReader.FromBuffer(req.Value)) dr.ReadBytes(buf);
            received = buf;
            Console.WriteLine($"[srv ] write {buf.Length} bytes offset={req.Offset} option={req.Option}");
            if (req.Option == GattWriteOption.WriteWithResponse) req.Respond();
        };

        provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
        {
            IsDiscoverable = true,
            IsConnectable = true,
        });
        await Task.Delay(1500);
        Console.WriteLine($"[srv ] AdvertisementStatus = {provider.AdvertisementStatus}");

        // --- client side: connect to ourselves -------------------------------
        try
        {
            Console.WriteLine("[cli ] FromBluetoothAddressAsync(own) ...");
            BluetoothLEDevice device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (device is null)
            {
                Console.WriteLine("[cli ] -> null (self-connect not supported)");
                return 2;
            }
            Console.WriteLine($"[cli ] device name='{device.Name}' status={device.ConnectionStatus}");

            GattDeviceServicesResult services = await device.GetGattServicesForUuidAsync(ServiceUuid);
            Console.WriteLine($"[cli ] GetGattServicesForUuidAsync -> {services.Status}, {services.Services.Count} service(s)");
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            {
                Console.WriteLine("[cli ] self-connect not usable on this adapter");
                return 2;
            }

            GattDeviceService svc = services.Services[0];
            GattCharacteristicsResult chars = await svc.GetCharacteristicsForUuidAsync(CharAUuid);
            Console.WriteLine($"[cli ] chars A -> {chars.Status}, {chars.Characteristics.Count}");
            if (chars.Characteristics.Count > 0)
            {
                GattReadResult read = await chars.Characteristics[0].ReadValueAsync();
                CryptographicBuffer.CopyToByteArray(read.Value, out byte[] value);
                Console.WriteLine($"[cli ] read challenge -> {read.Status}, {value.Length} bytes, " +
                                  $"match={value.AsSpan().SequenceEqual(challenge)}");
            }

            GattCharacteristicsResult charsB = await svc.GetCharacteristicsForUuidAsync(CharBUuid);
            if (charsB.Characteristics.Count > 0)
            {
                GattCharacteristic b = charsB.Characteristics[0];
                GattCommunicationStatus sub = await b.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                _ = sub;
                GattWriteResult w = await b.WriteValueWithResultAsync(
                    CryptographicBuffer.CreateFromByteArray(new byte[] { 1, 2, 3, 4 }));
                Console.WriteLine($"[cli ] write -> {w.Status}");
                await Task.Delay(500);
                Console.WriteLine($"[cli ] server received {received.Length} bytes: {Convert.ToHexString(received)}");
                Console.WriteLine(notified ? "[cli ] notification received" : "[cli ] no notification seen");
            }

            device.Dispose();
            Console.WriteLine("=== LOOPBACK: SELF-CONNECT WORKED ===");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[cli ] {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
        finally
        {
            try { provider.StopAdvertising(); } catch { }
        }
    }
}
