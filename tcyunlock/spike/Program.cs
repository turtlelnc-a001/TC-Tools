// SPIKE ONLY - validates that .NET 8 can consume the WinRT BLE GATT Server APIs and
// that a real GATT service can actually be published + advertised on this machine.
// Not part of the shipping tool.
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

internal static class SpikeProgram
{
    private static readonly Guid ServiceUuid = new Guid("7a1c9e40-2f3d-4b6c-9a11-6c5d3e8f2b01");
    private static readonly Guid CharAUuid = new Guid("7a1c9e41-2f3d-4b6c-9a11-6c5d3e8f2b01");
    private static readonly Guid CharBUuid = new Guid("7a1c9e42-2f3d-4b6c-9a11-6c5d3e8f2b01");

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--api") return ApiDump.Run();
        if (args.Length > 0 && args[0] == "--radio") return await RadioProbe.RunAsync(args);
        if (args.Length > 0 && args[0] == "--loopback") return await LoopbackProbe.RunAsync();

        Console.WriteLine("=== SPIKE: WinRT BLE GATT Server on .NET 8 ===");
        Console.WriteLine($"Runtime       : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"OSDescription : {RuntimeInformation.OSDescription}");
        Console.WriteLine();

        int failures = 0;

        // ---- (0) radio present? -------------------------------------------------
        try
        {
            BluetoothAdapter? adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null)
            {
                Console.WriteLine("[FAIL] BluetoothAdapter.GetDefaultAsync() -> null (no radio)");
                failures++;
            }
            else
            {
                Console.WriteLine($"[ ok ] Adapter: {adapter.BluetoothAddress:X12} LE={adapter.IsLowEnergySupported} " +
                                  $"Central={adapter.IsCentralRoleSupported} Peripheral={adapter.IsPeripheralRoleSupported} " +
                                  $"AdvOffload={adapter.IsAdvertisementOffloadSupported}");
                if (!adapter.IsPeripheralRoleSupported)
                {
                    Console.WriteLine("[WARN] IsPeripheralRoleSupported == false -> GATT server may not work");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] BluetoothAdapter: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }

        Console.WriteLine();
        Console.WriteLine("[info] WinRT GenericAttributeProfile/Advertisement namespaces projected OK (types compiled)");

        // ---- (b) GattServiceProvider create + advertise -------------------------
        GattServiceProvider? provider = null;
        try
        {
            GattServiceProviderResult result = await GattServiceProvider.CreateAsync(ServiceUuid);
            Console.WriteLine($"[info] GattServiceProvider.CreateAsync -> Error={result.Error} " +
                              $"ServiceProvider={(result.ServiceProvider is null ? "<null>" : "ok")}");
            if (result.ServiceProvider is null)
            {
                Console.WriteLine("[FAIL] CreateAsync did not return a ServiceProvider");
                failures++;
            }
            else
            {
                provider = result.ServiceProvider;

                GattLocalCharacteristicResult charA = await provider.Service.CreateCharacteristicAsync(
                    CharAUuid,
                    new GattLocalCharacteristicParameters
                    {
                        CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
                        ReadProtectionLevel = GattProtectionLevel.Plain,
                        WriteProtectionLevel = GattProtectionLevel.Plain,
                        UserDescription = "challenge",
                    });
                Console.WriteLine($"[info] CreateCharacteristicAsync(A) -> Error={charA.Error} " +
                                  $"Char={(charA.Characteristic is null ? "<null>" : "ok")}");

                GattLocalCharacteristicResult charB = await provider.Service.CreateCharacteristicAsync(
                    CharBUuid,
                    new GattLocalCharacteristicParameters
                    {
                        CharacteristicProperties = GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse,
                        ReadProtectionLevel = GattProtectionLevel.Plain,
                        WriteProtectionLevel = GattProtectionLevel.Plain,
                        UserDescription = "command",
                    });
                Console.WriteLine($"[info] CreateCharacteristicAsync(B) -> Error={charB.Error} " +
                                  $"Char={(charB.Characteristic is null ? "<null>" : "ok")}");

                GattLocalCharacteristic? a = charA.Characteristic;
                GattLocalCharacteristic? b = charB.Characteristic;

                if (a is not null)
                {
                    // Event-driven, 1709-safe handlers.
                    a.ReadRequested += async (sender, args) =>
                    {
                        GattReadRequest request = await args.GetRequestAsync();
                        var nonce = new byte[32];
                        for (int i = 0; i < nonce.Length; i++) nonce[i] = (byte)i;
                        request.RespondWithValue(CryptographicBuffer.CreateFromByteArray(nonce));
                    };
                    a.SubscribedClientsChanged += (sender, args) =>
                        Console.WriteLine($"[evt] SubscribedClientsChanged -> {sender.SubscribedClients.Count} client(s)");
                }

                if (b is not null)
                {
                    b.WriteRequested += async (sender, args) =>
                    {
                        GattWriteRequest request = await args.GetRequestAsync();
                        var buffer = new byte[request.Value.Length];
                        using (var reader = DataReader.FromBuffer(request.Value))
                        {
                            reader.ReadBytes(buffer);
                        }
                        Console.WriteLine($"[evt] WriteRequested -> {buffer.Length} byte(s), state={request.State}");

                        if (request.Option == GattWriteOption.WriteWithResponse)
                        {
                            request.Respond();
                        }
                    };
                }

                // Notify API shape check: NotifyValueAsync must accept an IBuffer (1709 overload).
                if (a is not null)
                {
                    try
                    {
                        var notify = await a.NotifyValueAsync(
                            CryptographicBuffer.CreateFromByteArray(new byte[] { 1, 2, 3 }));
                        Console.WriteLine($"[info] NotifyValueAsync(IBuffer) -> {notify?.GetType().Name} / {notify} " +
                                          "(client-not-subscribed is expected; proves the API binds)");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[warn] NotifyValueAsync: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                // Advertise
                provider.AdvertisementStatusChanged += (sender, args) =>
                    Console.WriteLine($"[evt] AdvertisementStatusChanged -> {sender.AdvertisementStatus} (error={args.Error})");

                provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
                {
                    IsDiscoverable = true,
                    IsConnectable = true,
                });
                Console.WriteLine("[info] StartAdvertising() called");

                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(500);
                    Console.WriteLine($"[info] t={(i + 1) * 500}ms AdvertisementStatus={provider.AdvertisementStatus}");
                    if (provider.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started) break;
                }

                if (provider.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started)
                {
                    Console.WriteLine("[ ok ] >>> ADVERTISING IS LIVE (status=Started) <<<");
                }
                else
                {
                    Console.WriteLine($"[FAIL] AdvertisementStatus={provider.AdvertisementStatus} (expected Started)");
                    failures++;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] GATT server: {ex}");
            failures++;
        }
        finally
        {
            try { provider?.StopAdvertising(); } catch { }
        }

        // ---- (c) BluetoothLEAdvertisementPublisher ------------------------------
        Console.WriteLine();
        try
        {
            var publisher = new BluetoothLEAdvertisementPublisher();
            publisher.Advertisement.LocalName = "TCUNLOCK-SPIKE";
            publisher.Advertisement.ServiceUuids.Add(ServiceUuid);
            publisher.StatusChanged += (sender, args) =>
                Console.WriteLine($"[evt] Publisher.StatusChanged -> {sender.Status} (error={args.Error})");
            publisher.Start();
            await Task.Delay(1500);
            Console.WriteLine($"[info] BluetoothLEAdvertisementPublisher.Status = {publisher.Status}");
            Console.WriteLine(publisher.Status == BluetoothLEAdvertisementPublisherStatus.Started
                ? "[ ok ] BluetoothLEAdvertisementPublisher is live"
                : "[warn] BluetoothLEAdvertisementPublisher not started");
            publisher.Stop();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[warn] BluetoothLEAdvertisementPublisher: {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "=== SPIKE RESULT: ALL GREEN ==="
            : $"=== SPIKE RESULT: {failures} FAILURE(S) ===");
        return failures == 0 ? 0 : 1;
    }
}
