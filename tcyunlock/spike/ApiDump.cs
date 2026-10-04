// SPIKE helper: dumps the real WinRT projection surface (exact member names +
// [SupportedOSPlatform] minimum-OS annotations) so the shipping code can be
// written against verified APIs instead of recalled ones.
using System.Reflection;
using System.Runtime.InteropServices;

internal static class ApiDump
{
    private static readonly string[] Interesting =
    {
        "Windows.Devices.Bluetooth.BluetoothAdapter",
        "Windows.Devices.Bluetooth.BluetoothLEDevice",
        "Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementPublisher",
        "Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisement",
        "Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProvider",
        "Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProviderResult",
        "Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProviderAdvertisingParameters",
        "Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProviderAdvertisementStatus",
        "Windows.Devices.Bluetooth.GenericAttributeProfile.GattLocalService",
        "Windows.Devices.Bluetooth.GenericAttributeProfile.GattLocalCharacteristic",
        "Windows.Devices.Bluetooth.GenericAttributeProfile.GattLocalCharacteristicParameters",
        "Windows.Devices.Bluetooth.GenericAttributeProfile.GattLocalCharacteristicResult",
        "Windows.Devices.Bluetooth.GenericAttributeProfile.GattReadRequest",
        "Windows.Devices.Bluetooth.GenericAttributeProfile.GattWriteRequest",
        "Windows.Devices.Bluetooth.GenericAttributeProfile.GattWriteOption",
        "Windows.Devices.Bluetooth.GenericAttributeProfile.GattWriteRequestState",
        "Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus",
        "Windows.Devices.Bluetooth.GenericAttributeProfile.GattProtectionLevel",
        "Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties",
        "Windows.Devices.Bluetooth.GenericAttributeProfile.GattSubscribedClient",
        "Windows.Security.Cryptography.CryptographicBuffer",
        "Windows.Storage.Streams.DataWriter",
        "Windows.Storage.Streams.DataReader",
    };

    private static readonly string[] NameFilters =
    {
        "GattServiceProviderError",
        "GattClientCharacteristicConfigurationDescriptor",
        "GattCharacteristicProperties",
        "GattServiceProviderAdvertisementStatusChanged",
        "GattReadRequestedEventArgs",
        "GattWriteRequestedEventArgs",
    };

    public static int Run()
    {
        Assembly asm = typeof(Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProvider).Assembly;
        Console.WriteLine($"Projection assembly : {asm.GetName().Name} {asm.GetName().Version}");
        Console.WriteLine($"Location            : {asm.Location}");
        Console.WriteLine($"Runtime             : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"OSVersion           : {Environment.OSVersion.Version}");
        Console.WriteLine();

        Type[] exported;
        try { exported = asm.GetExportedTypes(); }
        catch (ReflectionTypeLoadException ex) { exported = ex.Types.Where(t => t is not null).ToArray()!; }

        Console.WriteLine("### Type-name search");
        foreach (string filter in NameFilters)
        {
            Console.WriteLine($"-- matching '{filter}':");
            foreach (Type t in exported.Where(t => t.Name.Contains(filter, StringComparison.Ordinal)).OrderBy(t => t.FullName))
            {
                Console.WriteLine($"   {t.FullName}  ({Kind(t)})  {MinOs(t)}");
            }
        }
        Console.WriteLine();

        Console.WriteLine("### Member dump");
        foreach (string name in Interesting)
        {
            Type? t = asm.GetType(name) ?? exported.FirstOrDefault(x => x.FullName == name);
            if (t is null)
            {
                Console.WriteLine($"!! {name} : NOT FOUND");
                continue;
            }

            Console.WriteLine($"== {t.FullName}  ({Kind(t)})  {MinOs(t)}");

            if (t.IsEnum)
            {
                foreach (string v in Enum.GetNames(t))
                    Console.WriteLine($"     {v} = {Convert.ToInt64(Enum.Parse(t, v))}");
                Console.WriteLine();
                continue;
            }

            foreach (MemberInfo m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                                        .OrderBy(m => m.MemberType).ThenBy(m => m.Name))
            {
                switch (m)
                {
                    case ConstructorInfo c:
                        Console.WriteLine($"     .ctor({Params(c.GetParameters())})  {MinOs(c)}");
                        break;
                    case MethodInfo mi when !mi.IsSpecialName:
                        string async = AsyncKind(mi.ReturnType);
                        Console.WriteLine($"     {mi.Name}({Params(mi.GetParameters())}) -> {Pretty(mi.ReturnType)}{async}  {MinOs(mi)}");
                        break;
                    case PropertyInfo pi:
                        Console.WriteLine($"     {pi.Name} : {Pretty(pi.PropertyType)}  {MinOs(pi)}");
                        break;
                    case EventInfo ei:
                        Console.WriteLine($"     event {ei.Name} : {Pretty(ei.EventHandlerType!)}  {MinOs(ei)}");
                        break;
                }
            }
            Console.WriteLine();
        }

        return 0;
    }

    private static string Kind(Type t)
        => t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : "class";

    private static string Params(ParameterInfo[] ps)
        => string.Join(", ", ps.Select(p => $"{Pretty(p.ParameterType)} {p.Name}"));

    private static string Pretty(Type t)
    {
        if (!t.IsGenericType) return t.Name;
        string baseName = t.Name.Split('`')[0];
        return $"{baseName}<{string.Join(", ", t.GetGenericArguments().Select(Pretty))}>";
    }

    private static string AsyncKind(Type t)
        => t.IsGenericType && t.Name.StartsWith("IAsyncOperation", StringComparison.Ordinal) ? "  [WinRT-async]" : string.Empty;

    /// <summary>[SupportedOSPlatform("windows10.0.16299.0")] -> "min=16299".</summary>
    private static string MinOs(MemberInfo m)
    {
        try
        {
            foreach (CustomAttributeData a in m.GetCustomAttributesData())
            {
                if (!a.AttributeType.Name.Contains("SupportedOSPlatform", StringComparison.Ordinal)) continue;
                if (a.ConstructorArguments.Count == 1 && a.ConstructorArguments[0].Value is string s)
                    return $"  [minOS {s}]";
            }
        }
        catch (Exception) { }
        return string.Empty;
    }
}
