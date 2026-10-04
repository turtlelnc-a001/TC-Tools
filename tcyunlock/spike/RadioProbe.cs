// SPIKE helper: why is GattServiceProvider saying RadioNotAvailable?
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;

internal static class RadioProbe
{
    private static readonly Guid ServiceUuid = new("7a1c9e40-2f3d-4b6c-9a11-6c5d3e8f2b01");

    public static async Task<int> RunAsync(string[] args)
    {
        Console.WriteLine("=== RADIO PROBE ===");
        bool tryTurnOn = args.Contains("--on");

        // 1. Radios known to the Radio Management API (this is what the
        //    Bluetooth toggle in Settings flips).
        try
        {
            IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync();
            Console.WriteLine($"[info] Radio.GetRadiosAsync() -> {radios.Count} radio(s)");
            foreach (Radio r in radios)
            {
                Console.WriteLine($"   kind={r.Kind,-12} state={r.State,-8} name='{r.Name}'");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[warn] Radio.GetRadiosAsync: {ex.GetType().Name}: {ex.Message}");
        }

        // 2. Radio access status for this app.
        try
        {
            RadioAccessStatus access = await Radio.RequestAccessAsync();
            Console.WriteLine($"[info] Radio.RequestAccessAsync() -> {access}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[warn] Radio.RequestAccessAsync: {ex.GetType().Name}: {ex.Message}");
        }

        // 3. Adapter again, plus the raw WinRT error from CreateAsync.
        try
        {
            BluetoothAdapter? adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null)
            {
                Console.WriteLine("[warn] no default adapter");
            }
            else
            {
                Console.WriteLine($"[info] adapter {adapter.BluetoothAddress:X12} LE={adapter.IsLowEnergySupported} " +
                                  $"Peripheral={adapter.IsPeripheralRoleSupported} Central={adapter.IsCentralRoleSupported}");
                Radio? radio = await adapter.GetRadioAsync();
                Console.WriteLine(radio is null
                    ? "[warn] adapter.GetRadioAsync() -> null"
                    : $"[info] adapter radio: kind={radio.Kind} state={radio.State} name='{radio.Name}'");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[warn] adapter: {ex.GetType().Name}: {ex.Message}");
        }

        // 4. GattServiceProvider, with the exact WinRT error surfaced.
        try
        {
            GattServiceProviderResult res = await GattServiceProvider.CreateAsync(ServiceUuid);
            Console.WriteLine($"[info] GattServiceProvider.CreateAsync -> Error={res.Error} ({(int)res.Error}) " +
                              $"provider={(res.ServiceProvider is null ? "<null>" : "ok")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[fail] CreateAsync threw {ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8})");
        }

        // 5. If asked, try to flip the Bluetooth radio on programmatically.
        if (tryTurnOn)
        {
            try
            {
                IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync();
                Radio? bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                if (bt is null)
                {
                    Console.WriteLine("[warn] no Bluetooth radio to turn on");
                }
                else
                {
                    Console.WriteLine($"[info] SetStateAsync(On) on '{bt.Name}' (was {bt.State}) ...");
                    RadioAccessStatus st = await bt.SetStateAsync(RadioState.On);
                    await Task.Delay(1500);
                    IReadOnlyList<Radio> after = await Radio.GetRadiosAsync();
                    Radio? bt2 = after.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                    Console.WriteLine($"[info] SetStateAsync -> {st}; radio now {bt2?.State}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[warn] SetStateAsync: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 6. Can we at least SCAN for LE advertisements? This separates
        //    "radio off" from "advertising specifically blocked".
        try
        {
            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active,
            };
            var seen = new List<string>();
            watcher.Received += (s, e) =>
            {
                string name = e.Advertisement.LocalName;
                if (!string.IsNullOrEmpty(name)) seen.Add(name);
            };
            watcher.Stopped += (s, e) =>
                Console.WriteLine($"[scan] watcher stopped: {e.Error}");
            watcher.Start();
            Console.WriteLine($"[scan] watcher status after Start(): {watcher.Status}");
            await Task.Delay(4000);
            watcher.Stop();
            Console.WriteLine($"[scan] advertisements with a local name seen: {seen.Count} -> " +
                              string.Join(", ", seen.Distinct().Take(8)));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[scan] {ex.GetType().Name}: {ex.Message}");
        }

        // 7. GattServiceProvider again, with exact WinRT error surfaced.
        try
        {
            GattServiceProviderResult res = await GattServiceProvider.CreateAsync(ServiceUuid);
            Console.WriteLine($"[info] GattServiceProvider.CreateAsync -> Error={res.Error} ({(int)res.Error}) " +
                              $"provider={(res.ServiceProvider is null ? "<null>" : "ok")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[fail] CreateAsync threw {ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8})");
        }

        // 8. Advertisement publisher, smallest possible payloads, to separate
        //    "radio off" from "advertisement payload too large".
        await TryPublisher("flags-only", a => { a.Flags = BluetoothLEAdvertisementFlags.GeneralDiscoverableMode; });
        await TryPublisher("local-name-only", a => { a.LocalName = "TCUNLOCK"; });
        await TryPublisher("service-uuid-only", a => { a.ServiceUuids.Add(ServiceUuid); });
        await TryPublisher("name+uuid (oversized)", a =>
        {
            a.LocalName = "TCUNLOCK-SPIKE";
            a.ServiceUuids.Add(ServiceUuid);
        });

        return 0;
    }

    private static async Task TryPublisher(string label, Action<BluetoothLEAdvertisement> configure)
    {
        try
        {
            var pub = new BluetoothLEAdvertisementPublisher();
            configure(pub.Advertisement);
            pub.Start();
            await Task.Delay(700);
            Console.WriteLine($"[adv ] {label,-24} -> Status={pub.Status}");
            pub.Stop();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[adv ] {label,-24} -> {ex.GetType().Name}: {ex.Message}");
        }
    }
}
