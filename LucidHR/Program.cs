using System;
using System.Globalization;
using System.IO;
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
        private const float timescale = 1000;
        private const bool testing = true;
        private const bool record = true;
        private const float staleDataTime = 20;
        private const float stalenessCatchupFactor = 0.1f;

        private const int hrMin = 40;
        private const int hrMax = 100;
        private const float rriMin = 60f / hrMax;
        private const float rriMax = 60f / hrMin;

        private static string filename;

        static async Task Main(string[] args)
        {
            if (testing)
            {
                args = new string[] { @"C:\Workspace\LucidHR\2021_05_11-1_01.csv" };
            }

            if (args.Count() > 0)
            {
                if (File.Exists(args[0]))
                {
                    filename = Path.GetFileName(args[0]);

                    if (File.Exists(filename + "_filtered.csv")) File.Delete(filename + "_filtered.csv");

                    string text = File.ReadAllText(args[0]);

                    string[] rows = text.Split(Environment.NewLine);
                    string[][] table = rows.Select(x => x.Split(",")).ToArray();

                    long lastTimestamp = -1;

                    for (int i = 0; i < table.GetLength(0); i++)
                    {
                        if (table[i].Length == 4)
                        {
                            long currentTimestamp = Int64.Parse(table[i][1]);
                            if (lastTimestamp != -1) await Task.Delay((int)((currentTimestamp - lastTimestamp) * (1000 / timescale)));
                            lastTimestamp = currentTimestamp;

                            Console.WriteLine(table[i][0] + " " + table[i][2] + " " + table[i][3]);
                            Evaluate(currentTimestamp, Int32.Parse(table[i][2]), float.Parse(table[i][3]));
                            Console.WriteLine();
                        }
                    }
                }
            }
            else
            {
                AppDomain.CurrentDomain.ProcessExit += new EventHandler(CurrentDomain_ProcessExit);

                filename = DateTime.Now.ToString("yyyy_MM_dd-h_mm");

                Console.WriteLine("Starting file " + filename);

                if (await Connect())
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
                    Console.ReadKey();
                }
            }
        }

        private static long lastInitialTimestamp = 0;
        private static float rriAccumulator = 0;

        private static string Evaluate(long timestamp, int hr, float rri)
        {
            string ret = "";

            if (hr >= hrMin && hr <= hrMax && rri >= rriMin && rri <= rriMax)
            {
                rriAccumulator += rri;
                float error = timestamp - lastInitialTimestamp - rriAccumulator;
                Console.WriteLine("Error: " + error);
                if (Math.Abs(error) > staleDataTime)
                {
                    rriAccumulator = 0;
                    lastInitialTimestamp = timestamp;
                    Console.WriteLine("Stale Data Detected at " + timestamp);
                }
                else
                {
                    rriAccumulator += error * stalenessCatchupFactor;
                }

                if (testing)
                {
                    using (StreamWriter w = File.AppendText(filename + "_filtered.csv"))
                    {
                        w.WriteLine(timestamp + "," + hr + "," + rri);
                    }
                }
            }
            else
            {
                using (StreamWriter w = File.AppendText(filename + "_filtered.csv"))
                {
                    w.WriteLine(timestamp + "," + 120 + "," + 0.5f);
                }
            }
            return ret;
        }


        private static BluetoothLEDevice bleDevice;
        private static GattCharacteristic characteristic;

        private static async Task<bool> Connect()
        {
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
                            Console.WriteLine("Found Paired Heart Rate Device");

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
            return count > 0;
        }

        private static void ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            try
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

                        long timestamp = DateTimeOffset.Now.ToUnixTimeSeconds();

                        Console.WriteLine("RRI: " + rri / 1024f);
                        string res = Evaluate(timestamp, heartRate, (rri / 1024f));

                        if (record)
                        {
                            using (StreamWriter w = File.AppendText(filename + ".csv"))
                            {
                                w.WriteLine("\"" + DateTime.Now.ToString("T") + "\"" + "," + timestamp + "," + heartRate + "," + (rri / 1024f).ToString(CultureInfo.InvariantCulture) + res);
                            }
                        }
                    }
                }
            }
            catch { }
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
