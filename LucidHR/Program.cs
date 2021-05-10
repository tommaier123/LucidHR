using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace LucidHR
{
    class Program
    {

        public static BluetoothLEDevice bleDevice;
        public static GattCharacteristic characteristic;

        static async Task Main()
        {
            AppDomain.CurrentDomain.ProcessExit += new EventHandler(CurrentDomain_ProcessExit);

            int count = 0;

            foreach (var device in await DeviceInformation.FindAllAsync())
            {
                try
                {
                    bleDevice = await BluetoothLEDevice.FromIdAsync(device.Id);

                    if (bleDevice != null && bleDevice.Appearance.Category == BluetoothLEAppearanceCategories.HeartRate)
                    {
                        GattDeviceService service = bleDevice.GetGattService(new Guid("0000180d-0000-1000-8000-00805f9b34fb"));
                        characteristic = service.GetCharacteristics(new Guid("00002a37-0000-1000-8000-00805f9b34fb")).First();

                        if (service != null && characteristic != null)
                        {
                            Console.WriteLine("Found Device");

                            GattCommunicationStatus status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
                            if (status == GattCommunicationStatus.Success)
                            {
                                bleDevice.ConnectionStatusChanged += ConnectionStatusChanged;
                                characteristic.ValueChanged += ValueChanged;
                                count++;
                                Console.WriteLine("Subscribed to Heart Rate");
                                break;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    //Console.WriteLine(e.Message);
                }
            }
            if (count > 0)
            {
                while (true)
                {
                    await Task.Delay(1000);
                }
            }
            else
            {
                Console.WriteLine("No suitable device found");
                Console.WriteLine();
                Console.WriteLine("Press any key to exit...");
                Console.Read();
            }
        }

        private static void ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);

            byte flags = reader.ReadByte();

            int heartRate = -1;

            if ((flags & (1 << 0)) != 0)//16bit HR
            {
                byte a = reader.ReadByte();
                byte b = reader.ReadByte();
                heartRate = b << 8 | a;
            }
            else//8bit HR
            {
                heartRate = reader.ReadByte();
            }

            Console.WriteLine("Heart Rate: " + heartRate);

            if ((flags & (1 << 3)) != 0)//Energy Expended present (16bit)
            {
                byte a = reader.ReadByte();
                byte b = reader.ReadByte();
                int energyExpended = b << 8 | a;

                Console.WriteLine(energyExpended);
            }

            if ((flags & (1 << 4)) != 0)//RRI present (16bit)
            {
                while (reader.UnconsumedBufferLength > 1)
                {
                    byte a = reader.ReadByte();
                    byte b = reader.ReadByte();
                    int rri = b << 8 | a;

                    Console.WriteLine("RRI: " + rri/1024f);
                }
            }
        }

        private static void ConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            Console.WriteLine("Connection status: " + sender.ConnectionStatus);
        }

        private static void CurrentDomain_ProcessExit(object sender, EventArgs e)
        {
            if (bleDevice != null)
            {
                bleDevice.ConnectionStatusChanged -= ConnectionStatusChanged;
                characteristic.ValueChanged -= ValueChanged;
                bleDevice.Dispose();
            }
        }
    }
}
