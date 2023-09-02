using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Text;

namespace EverdriveUsbTest
{
    internal class Program
    {
        private const int DefaultWriteLength = 32768; // = 0x8000

        private const int WriteRomTargetAddress = 0x10000000;

        private const string Command_Test_Send = "cmdt";
        private const string Command_Test_Response = "cmdr";
        private const string Command_WriteRom = "cmdW";
        private const string Command_PifBoot_Send = "cmds";

        private static object _lock = new object();

        private static SerialPort? _serialPort = null;
        private static Queue<byte> _readQueue = new Queue<byte>();

        private static int _globalWriteLength = 0;

        static void Main(string[] args)
        {
            var ports = SerialPort.GetPortNames();
            foreach (var port in ports)
            {
                Console.WriteLine($"serial port: {port}");
            }

            var usePort = "COM5";

            _serialPort = new SerialPort(usePort);
            _serialPort.DataReceived += DataReceived;
            _serialPort.Open();

            SendEverdriveCommand(Command_Test_Send, 0, 0, 0);
            System.Threading.Thread.Sleep(100);

            Console.WriteLine($"Sending command: {Command_Test_Send}");

            string commandResponse;

            var response = Read();
            if (!object.ReferenceEquals(null, response))
            {
                commandResponse = System.Text.Encoding.ASCII.GetString(response);
                Console.WriteLine($"response: {commandResponse}");
            }
            else
            {
                Console.WriteLine($"no response");
                
                return;
            }

            var sendFileData = true;
            if (sendFileData)
            {
                var filedata = System.IO.File.ReadAllBytes("sm64.z64");

                // Read the ROM header to check if its byteswapped
                if (!(filedata[0] == 0x80 && filedata[1] == 0x37 && filedata[2] == 0x12 && filedata[3] == 0x40))
                {
                    for (var j = 0; j < filedata.Length; j += 2)
                    {
                        filedata[j] ^= filedata[j + 1];
                        filedata[j + 1] ^= filedata[j];
                        filedata[j] ^= filedata[j + 1];
                    }
                }

                var size = filedata.Length;

                UInt32 padSize = BitUtility.CalculatePadsize((UInt32)size);
                var bytesLeft = (int)padSize;
                int bytesDo;
                int bytesDone = 0;

                // if padded size is different from rom size, resize the file buffer.
                if (size != (int)padSize)
                {
                    var newFileData = new byte[(int)padSize];
                    Array.Copy(filedata, newFileData, size);
                    filedata = newFileData;
                }

                Console.WriteLine($"Sending command: {Command_WriteRom}, write length: {bytesLeft}");
                SendEverdriveCommand(Command_WriteRom, WriteRomTargetAddress, bytesLeft, 0);

                _globalWriteLength = 0;

                while (true)
                {
                    if (bytesLeft >= DefaultWriteLength)
                    {
                        bytesDo = DefaultWriteLength;
                    }
                    else
                    {
                        bytesDo = bytesLeft;
                    }

                    // End if we've got nothing else to send
                    if (bytesDo <= 0)
                        break;

                    // Try to send chunks
                    var sendBuffer = new byte[bytesDo];
                    Array.Copy(filedata, bytesDone, sendBuffer, 0, bytesDo);

                    Write(sendBuffer);

                    bytesLeft -= bytesDo;
                    bytesDone += bytesDo;

                    double percentDone = 100.0 * (double)bytesDone / (double)((int)padSize);

                    Console.WriteLine($"loop: sent {bytesDone} out of {(int)padSize} = {percentDone:0.00}%, {bytesLeft} remain");
                }

                var serialPortWriteLength = _globalWriteLength;
                Console.WriteLine($"serialPortWriteLength: {serialPortWriteLength}");
            }

            var startSpin = System.Diagnostics.Stopwatch.StartNew();
            int spinCount = 0;

            while (true)
            {
                Console.WriteLine($"Sending command: {Command_Test_Send}");
                SendEverdriveCommand(Command_Test_Send, 0, 0, 0);
                System.Threading.Thread.Sleep(100);
                response = Read();
                if (!object.ReferenceEquals(null, response))
                {
                    commandResponse = System.Text.Encoding.ASCII.GetString(response);
                    Console.WriteLine($"response: {commandResponse}");

                    break;
                }
                else
                {
                    Console.WriteLine($"no response");
                    spinCount++;
                    System.Threading.Thread.Sleep(10);
                }
            }

            startSpin.Stop();

            Console.WriteLine($"spin time: {startSpin.Elapsed.TotalSeconds}, count={spinCount}");

            System.Threading.Thread.Sleep(500);

            SendEverdriveCommand(Command_PifBoot_Send, 0, 0, 0);
            Console.WriteLine($"Sending command: {Command_PifBoot_Send}");
        }

        private static void DataReceived(object s, SerialDataReceivedEventArgs e)
        {
            byte[] data = new byte[_serialPort!.BytesToRead];
            _serialPort.Read(data, 0, data.Length);
            lock (_lock)
            {
                data.ToList().ForEach(b => _readQueue.Enqueue(b));
            }
        }

        private static byte[]? Read()
        {
            var result = new List<byte>();

            lock (_lock)
            {
                while (_readQueue.Count > 0)
                {
                    result.Add(_readQueue.Dequeue());
                }
            }

            if (!result.Any())
            {
                return null;
            }

            return result.ToArray();
        }

        private static void Write(byte[] data)
        {
            Write(data, 0, data.Length);
        }

        private static void Write(byte[] data, int offset, int length)
        {
            int remaining = length;

            while (remaining > 0)
            {
                var writeLength = DefaultWriteLength;
                if (writeLength > length)
                {
                    writeLength = length;
                }

                if (!_serialPort!.IsOpen)
                {
                    throw new InvalidOperationException("serial port is not open");
                }

                _serialPort!.Write(data, offset, writeLength);
                _globalWriteLength += writeLength;

                remaining -= writeLength;
                offset += writeLength;
            }
        }

        private static void SendEverdriveCommand(string commandText, Int32 address, Int32 size, Int32 arg)
        {
            if (string.IsNullOrEmpty(commandText))
            {
                throw new NullReferenceException($"{nameof(commandText)}");
            }

            if (commandText.Length != 4)
            {
                throw new ArgumentException($"Invalid everdrive command: {commandText}");
            }

            var sendBuffer = new byte[16];
            Array.Clear(sendBuffer, 0, sendBuffer.Length);

            int pos = 0;
            var cmdBytes = System.Text.Encoding.ASCII.GetBytes(commandText);
            foreach (var b in cmdBytes)
            {
                sendBuffer[pos++] = b;
            }

            BitUtility.Insert32Big(sendBuffer, pos, address);
            pos += sizeof(Int32);

            BitUtility.Insert32Big(sendBuffer, pos, size);
            pos += sizeof(Int32);

            BitUtility.Insert32Big(sendBuffer, pos, arg);
            pos += sizeof(Int32);

            Write(sendBuffer);
        }
    }

    public static class BitUtility
    {
        /// <summary>
        /// Converts 32 bit value to MSB and inserts into array at index.
        /// </summary>
        /// <param name="arr">Array to insert value into.</param>
        /// <param name="index">Index to insert value at.</param>
        /// <param name="value">Value to insert.</param>
        public static void Insert32Big(byte[] arr, Int32 index, Int32 value)
        {
            arr[index + 0] = (byte)((value >> 24) & 0xff);
            arr[index + 1] = (byte)((value >> 16) & 0xff);
            arr[index + 2] = (byte)((value >> 8) & 0xff);
            arr[index + 3] = (byte)(value & 0xff);
        }

        /// <summary>
        /// UNFLoader calc_padsize.
        /// </summary>
        /// <param name="size"></param>
        /// <returns></returns>
        public static UInt32 CalculatePadsize(UInt32 size)
        {
            size--;
            size |= size >> 1;
            size |= size >> 2;
            size |= size >> 4;
            size |= size >> 8;
            size |= size >> 16;
            size++;

            return size;
        }
    }
}