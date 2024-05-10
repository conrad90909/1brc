using static OneBillionAgain.Program;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Buffers;

namespace OneBillionAgain
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            var stopwatch = Stopwatch.StartNew();

            string filePath = @"C:\Code\OneBillion\measurements-1000000000.txt";

            var processor = new TemperatureDataProcessor();
            processor.ProcessFile(filePath); // Specify the path to your data file
            await processor.PrintStatistics(); // Await the PrintStatistics method

            stopwatch.Stop();
            Console.WriteLine($"File Load completed in {stopwatch.Elapsed.TotalSeconds} seconds.");
        }

        public struct TemperatureRecord
        {
            public string City;
            public float Temperature;

            public TemperatureRecord(string city)
            {
                City = city;
            }
        }

        public class TemperatureStats
        {
            public float MinTemperature = float.MaxValue;
            public float MaxTemperature = float.MinValue;
            public float TotalTemperature;
            public int Count;


            public void Aggregate(float temperature)
            {
                MinTemperature = Math.Min(MinTemperature, temperature);
                MaxTemperature = Math.Max(MaxTemperature, temperature);
                TotalTemperature += temperature;
                Count++;
            }

            public float MeanTemperature => TotalTemperature / Count;
        }

    }

    public class TemperatureDataProcessor
    {
        private ConcurrentDictionary<string, TemperatureStats> _temps = new ConcurrentDictionary<string, TemperatureStats>();


        public void ProcessFile(string filePath)
        {
            long fileSize = new FileInfo(filePath).Length;
            int numberOfProcessors = Environment.ProcessorCount - 1;
            long chunkSize = fileSize / numberOfProcessors;
            List<Task> processingTasks = new List<Task>();

            using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                (long start, long end)[] offsets = new (long, long)[numberOfProcessors];
                for (int i = 0; i < numberOfProcessors; i++)
                {
                    long startOffset = i * chunkSize;
                    long endOffset = (i == numberOfProcessors - 1) ? fileSize : (i + 1) * chunkSize;

                    if (i > 0) // Adjust start offset only for subsequent chunks
                        startOffset = AdjustOffsetToNextNewLine(fileStream, startOffset);

                    if (i < numberOfProcessors - 1) // Adjust end offset only for non-last chunks
                        endOffset = AdjustOffsetToNextNewLine(fileStream, endOffset);

                    offsets[i] = (startOffset, endOffset);
                }

                Parallel.For(0, numberOfProcessors, (i) =>
                {
                    using (var localStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        ProcessStream(localStream, offsets[i].start, offsets[i].end);
                    }
                });
            }
        }

        private void ProcessStream(FileStream stream, long startOffset, long endOffset)
        {
            stream.Seek(startOffset, SeekOrigin.Begin);
            byte[] buffer = new byte[65536];
            int bytesRead;
            List<byte> leftoverBytes = new List<byte>();  // To handle bytes that spill over buffer boundaries

            //Console.WriteLine($"Processing - {startOffset} to {endOffset}");

            while (stream.Position < endOffset)
            {
                bytesRead = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, endOffset - stream.Position));
                if (bytesRead > 0)
                {
                    // Handle any leftover bytes from the previous read
                    if (leftoverBytes.Count > 0)
                    {
                        byte[] newBuffer = new byte[leftoverBytes.Count + bytesRead];
                        leftoverBytes.CopyTo(newBuffer, 0);
                        Array.Copy(buffer, 0, newBuffer, leftoverBytes.Count, bytesRead);
                        bytesRead = newBuffer.Length; // Update bytesRead to include the leftover bytes
                        buffer = newBuffer; // Replace the original buffer with the new one
                    }

                    ProcessBuffer(new Span<byte>(buffer, 0, bytesRead));

                    // Prepare leftoverBytes for the next iteration
                    leftoverBytes.Clear();
                    int lastNewLine = Array.LastIndexOf(buffer, (byte)'\n', bytesRead - 1);
                    if (lastNewLine == -1 || lastNewLine != bytesRead - 1)
                    {
                        // If no newline found, or newline is not at the end, save the remainder
                        leftoverBytes.AddRange(buffer.Skip(lastNewLine + 1));
                    }
                }
            }
            // Process any remaining bytes if they form a complete line
            if (leftoverBytes.Count > 0)
            {
                ProcessBuffer(new Span<byte>(leftoverBytes.ToArray()));
            }
        }

          private int ProcessBuffer(Span<byte> buffer)
        { 
            int count = 0; 
            int index = 0;
            while (index < buffer.Length)
            {
                int delimiterIndex = buffer.Slice(index).IndexOf((byte)';');
                if (delimiterIndex == -1)
                {
                    // Break, no delimiter found
                    break;
                }

                var cityNameSpan = buffer.Slice(index, delimiterIndex);
                string cityName = Encoding.UTF8.GetString(cityNameSpan);

                index += delimiterIndex + 1;

                int tempEndIndex = buffer.Slice(index).IndexOf((byte)'\n');
                if (tempEndIndex == -1)
                {
                    // Break, no newline found.
                    break;
                }

                var temperatureSpan = buffer.Slice(index, tempEndIndex);
                index += tempEndIndex + 1;  // move past the newline

                float temperature = ParseTemperature(temperatureSpan);
                AggregateResults(cityName, temperature);
                count++;
            }
            return count;
        }

        private long AdjustOffsetToNextNewLine(FileStream fileStream, long startOffset)
        {
            const int bufferSize = 100;
            byte[] buffer = new byte[bufferSize];

            if (startOffset >= fileStream.Length)
                return fileStream.Length; // If start is beyond file length, return the end of file.

            fileStream.Seek(startOffset, SeekOrigin.Begin);

            long position = startOffset;
            int bytesRead;
            while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < bytesRead; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        long newPosition = position + i + 1;
                        //Console.WriteLine($"Newline found at buffer position {i}, file position {newPosition}");
                        return newPosition;
                    }
                }
                position += bytesRead; // Move the position forward by the number of bytes read.
            }

            //Console.WriteLine("Shouldn't happen!");
            // If no newline is found return original value.
            return startOffset;
        }


        private float ParseTemperature(Span<byte> temperature)
        {
            if (temperature.IsEmpty) return 0.0F;
            bool isNegative = temperature[0] == (byte)'-';
            int start = isNegative ? 1 : 0;

            float result = 0.0F;
            float divisor = 1.0F;
            bool fractionalPart = false;

            for (int i = start; i < temperature.Length; i++)
            {
                if (temperature[i] == (byte)'.')
                {
                    fractionalPart = true;
                    continue;
                }

                // Convert from ASCII to digit and add to result
                result = result * 10 + (temperature[i] - (byte)'0');

                if (fractionalPart)
                {
                    divisor *= 10.0F;
                }
            }

            result /= divisor;
            return isNegative ? -result : result;
        }

        public async Task PrintStatistics()
        {
            string outputFilePath = @"C:\Code\OneBillion\output_file_ffs.txt";
            long count = 0;
            try
            {
                using (var writer = new StreamWriter(outputFilePath))
                {
                    var cities = _temps.OrderBy(x => x.Key);
                    foreach (var city in cities)
                    {
                        count += city.Value.Count;
                        await writer.WriteLineAsync($"{city.Key};{city.Value.MinTemperature};{city.Value.MeanTemperature:F1};{city.Value.MaxTemperature}");
                    }
                }

                Console.WriteLine($"Lines total {count}");

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            
        }


        private void AggregateResults(string cityName, float temperature)
        {
            var stats = _temps.GetOrAdd(cityName, _ => new TemperatureStats());

            lock (stats)
            {
                stats.Aggregate(temperature);
            }
        }


        private void initialiseTemps()
        {
            _temps.TryAdd("Abha", new TemperatureStats());
            _temps.TryAdd("Abidjan", new TemperatureStats());
            _temps.TryAdd("Abéché", new TemperatureStats());
            _temps.TryAdd("Accra", new TemperatureStats());
            _temps.TryAdd("Addis Ababa", new TemperatureStats());
            _temps.TryAdd("Adelaide", new TemperatureStats());
            _temps.TryAdd("Aden", new TemperatureStats());
            _temps.TryAdd("Ahvaz", new TemperatureStats());
            _temps.TryAdd("Albuquerque", new TemperatureStats());
            _temps.TryAdd("Alexandra", new TemperatureStats());
            _temps.TryAdd("Alexandria", new TemperatureStats());
            _temps.TryAdd("Algiers", new TemperatureStats());
            _temps.TryAdd("Alice Springs", new TemperatureStats());
            _temps.TryAdd("Almaty", new TemperatureStats());
            _temps.TryAdd("Amsterdam", new TemperatureStats());
            _temps.TryAdd("Anadyr", new TemperatureStats());
            _temps.TryAdd("Anchorage", new TemperatureStats());
            _temps.TryAdd("Andorra la Vella", new TemperatureStats());
            _temps.TryAdd("Ankara", new TemperatureStats());
            _temps.TryAdd("Antananarivo", new TemperatureStats());
            _temps.TryAdd("Antsiranana", new TemperatureStats());
            _temps.TryAdd("Arkhangelsk", new TemperatureStats());
            _temps.TryAdd("Ashgabat", new TemperatureStats());
            _temps.TryAdd("Asmara", new TemperatureStats());
            _temps.TryAdd("Assab", new TemperatureStats());
            _temps.TryAdd("Astana", new TemperatureStats());
            _temps.TryAdd("Athens", new TemperatureStats());
            _temps.TryAdd("Atlanta", new TemperatureStats());
            _temps.TryAdd("Auckland", new TemperatureStats());
            _temps.TryAdd("Austin", new TemperatureStats());
            _temps.TryAdd("Baghdad", new TemperatureStats());
            _temps.TryAdd("Baguio", new TemperatureStats());
            _temps.TryAdd("Baku", new TemperatureStats());
            _temps.TryAdd("Baltimore", new TemperatureStats());
            _temps.TryAdd("Bamako", new TemperatureStats());
            _temps.TryAdd("Bangkok", new TemperatureStats());
            _temps.TryAdd("Bangui", new TemperatureStats());
            _temps.TryAdd("Banjul", new TemperatureStats());
            _temps.TryAdd("Barcelona", new TemperatureStats());
            _temps.TryAdd("Bata", new TemperatureStats());
            _temps.TryAdd("Batumi", new TemperatureStats());
            _temps.TryAdd("Beijing", new TemperatureStats());
            _temps.TryAdd("Beirut", new TemperatureStats());
            _temps.TryAdd("Belgrade", new TemperatureStats());
            _temps.TryAdd("Belize City", new TemperatureStats());
            _temps.TryAdd("Benghazi", new TemperatureStats());
            _temps.TryAdd("Bergen", new TemperatureStats());
            _temps.TryAdd("Berlin", new TemperatureStats());
            _temps.TryAdd("Bilbao", new TemperatureStats());
            _temps.TryAdd("Birao", new TemperatureStats());
            _temps.TryAdd("Bishkek", new TemperatureStats());
            _temps.TryAdd("Bissau", new TemperatureStats());
            _temps.TryAdd("Blantyre", new TemperatureStats());
            _temps.TryAdd("Bloemfontein", new TemperatureStats());
            _temps.TryAdd("Boise", new TemperatureStats());
            _temps.TryAdd("Bordeaux", new TemperatureStats());
            _temps.TryAdd("Bosaso", new TemperatureStats());
            _temps.TryAdd("Boston", new TemperatureStats());
            _temps.TryAdd("Bouaké", new TemperatureStats());
            _temps.TryAdd("Bratislava", new TemperatureStats());
            _temps.TryAdd("Brazzaville", new TemperatureStats());
            _temps.TryAdd("Bridgetown", new TemperatureStats());
            _temps.TryAdd("Brisbane", new TemperatureStats());
            _temps.TryAdd("Brussels", new TemperatureStats());
            _temps.TryAdd("Bucharest", new TemperatureStats());
            _temps.TryAdd("Budapest", new TemperatureStats());
            _temps.TryAdd("Bujumbura", new TemperatureStats());
            _temps.TryAdd("Bulawayo", new TemperatureStats());
            _temps.TryAdd("Burnie", new TemperatureStats());
            _temps.TryAdd("Busan", new TemperatureStats());
            _temps.TryAdd("Cabo San Lucas", new TemperatureStats());
            _temps.TryAdd("Cairns", new TemperatureStats());
            _temps.TryAdd("Cairo", new TemperatureStats());
            _temps.TryAdd("Calgary", new TemperatureStats());
            _temps.TryAdd("Canberra", new TemperatureStats());
            _temps.TryAdd("Cape Town", new TemperatureStats());
            _temps.TryAdd("Changsha", new TemperatureStats());
            _temps.TryAdd("Charlotte", new TemperatureStats());
            _temps.TryAdd("Chiang Mai", new TemperatureStats());
            _temps.TryAdd("Chicago", new TemperatureStats());
            _temps.TryAdd("Chihuahua", new TemperatureStats());
            _temps.TryAdd("Chișinău", new TemperatureStats());
            _temps.TryAdd("Chittagong", new TemperatureStats());
            _temps.TryAdd("Chongqing", new TemperatureStats());
            _temps.TryAdd("Christchurch", new TemperatureStats());
            _temps.TryAdd("City of San Marino", new TemperatureStats());
            _temps.TryAdd("Colombo", new TemperatureStats());
            _temps.TryAdd("Columbus", new TemperatureStats());
            _temps.TryAdd("Conakry", new TemperatureStats());
            _temps.TryAdd("Copenhagen", new TemperatureStats());
            _temps.TryAdd("Cotonou", new TemperatureStats());
            _temps.TryAdd("Cracow", new TemperatureStats());
            _temps.TryAdd("Da Lat", new TemperatureStats());
            _temps.TryAdd("Da Nang", new TemperatureStats());
            _temps.TryAdd("Dakar", new TemperatureStats());
            _temps.TryAdd("Dallas", new TemperatureStats());
            _temps.TryAdd("Damascus", new TemperatureStats());
            _temps.TryAdd("Dampier", new TemperatureStats());
            _temps.TryAdd("Dar es Salaam", new TemperatureStats());
            _temps.TryAdd("Darwin", new TemperatureStats());
            _temps.TryAdd("Denpasar", new TemperatureStats());
            _temps.TryAdd("Denver", new TemperatureStats());
            _temps.TryAdd("Detroit", new TemperatureStats());
            _temps.TryAdd("Dhaka", new TemperatureStats());
            _temps.TryAdd("Dikson", new TemperatureStats());
            _temps.TryAdd("Dili", new TemperatureStats());
            _temps.TryAdd("Djibouti", new TemperatureStats());
            _temps.TryAdd("Dodoma", new TemperatureStats());
            _temps.TryAdd("Dolisie", new TemperatureStats());
            _temps.TryAdd("Douala", new TemperatureStats());
            _temps.TryAdd("Dubai", new TemperatureStats());
            _temps.TryAdd("Dublin", new TemperatureStats());
            _temps.TryAdd("Dunedin", new TemperatureStats());
            _temps.TryAdd("Durban", new TemperatureStats());
            _temps.TryAdd("Dushanbe", new TemperatureStats());
            _temps.TryAdd("Edinburgh", new TemperatureStats());
            _temps.TryAdd("Edmonton", new TemperatureStats());
            _temps.TryAdd("El Paso", new TemperatureStats());
            _temps.TryAdd("Entebbe", new TemperatureStats());
            _temps.TryAdd("Erbil", new TemperatureStats());
            _temps.TryAdd("Erzurum", new TemperatureStats());
            _temps.TryAdd("Fairbanks", new TemperatureStats());
            _temps.TryAdd("Fianarantsoa", new TemperatureStats());
            _temps.TryAdd("Flores,  Petén", new TemperatureStats());
            _temps.TryAdd("Frankfurt", new TemperatureStats());
            _temps.TryAdd("Fresno", new TemperatureStats());
            _temps.TryAdd("Fukuoka", new TemperatureStats());
            _temps.TryAdd("Gabès", new TemperatureStats());
            _temps.TryAdd("Gaborone", new TemperatureStats());
            _temps.TryAdd("Gagnoa", new TemperatureStats());
            _temps.TryAdd("Gangtok", new TemperatureStats());
            _temps.TryAdd("Garissa", new TemperatureStats());
            _temps.TryAdd("Garoua", new TemperatureStats());
            _temps.TryAdd("George Town", new TemperatureStats());
            _temps.TryAdd("Ghanzi", new TemperatureStats());
            _temps.TryAdd("Gjoa Haven", new TemperatureStats());
            _temps.TryAdd("Guadalajara", new TemperatureStats());
            _temps.TryAdd("Guangzhou", new TemperatureStats());
            _temps.TryAdd("Guatemala City", new TemperatureStats());
            _temps.TryAdd("Halifax", new TemperatureStats());
            _temps.TryAdd("Hamburg", new TemperatureStats());
            _temps.TryAdd("Hamilton", new TemperatureStats());
            _temps.TryAdd("Hanga Roa", new TemperatureStats());
            _temps.TryAdd("Hanoi", new TemperatureStats());
            _temps.TryAdd("Harare", new TemperatureStats());
            _temps.TryAdd("Harbin", new TemperatureStats());
            _temps.TryAdd("Hargeisa", new TemperatureStats());
            _temps.TryAdd("Hat Yai", new TemperatureStats());
            _temps.TryAdd("Havana", new TemperatureStats());
            _temps.TryAdd("Helsinki", new TemperatureStats());
            _temps.TryAdd("Heraklion", new TemperatureStats());
            _temps.TryAdd("Hiroshima", new TemperatureStats());
            _temps.TryAdd("Ho Chi Minh City", new TemperatureStats());
            _temps.TryAdd("Hobart", new TemperatureStats());
            _temps.TryAdd("Hong Kong", new TemperatureStats());
            _temps.TryAdd("Honiara", new TemperatureStats());
            _temps.TryAdd("Honolulu", new TemperatureStats());
            _temps.TryAdd("Houston", new TemperatureStats());
            _temps.TryAdd("Ifrane", new TemperatureStats());
            _temps.TryAdd("Indianapolis", new TemperatureStats());
            _temps.TryAdd("Iqaluit", new TemperatureStats());
            _temps.TryAdd("Irkutsk", new TemperatureStats());
            _temps.TryAdd("Istanbul", new TemperatureStats());
            _temps.TryAdd("İzmir", new TemperatureStats());
            _temps.TryAdd("Jacksonville", new TemperatureStats());
            _temps.TryAdd("Jakarta", new TemperatureStats());
            _temps.TryAdd("Jayapura", new TemperatureStats());
            _temps.TryAdd("Jerusalem", new TemperatureStats());
            _temps.TryAdd("Johannesburg", new TemperatureStats());
            _temps.TryAdd("Jos", new TemperatureStats());
            _temps.TryAdd("Juba", new TemperatureStats());
            _temps.TryAdd("Kabul", new TemperatureStats());
            _temps.TryAdd("Kampala", new TemperatureStats());
            _temps.TryAdd("Kandi", new TemperatureStats());
            _temps.TryAdd("Kankan", new TemperatureStats());
            _temps.TryAdd("Kano", new TemperatureStats());
            _temps.TryAdd("Kansas City", new TemperatureStats());
            _temps.TryAdd("Karachi", new TemperatureStats());
            _temps.TryAdd("Karonga", new TemperatureStats());
            _temps.TryAdd("Kathmandu", new TemperatureStats());
            _temps.TryAdd("Khartoum", new TemperatureStats());
            _temps.TryAdd("Kingston", new TemperatureStats());
            _temps.TryAdd("Kinshasa", new TemperatureStats());
            _temps.TryAdd("Kolkata", new TemperatureStats());
            _temps.TryAdd("Kuala Lumpur", new TemperatureStats());
            _temps.TryAdd("Kumasi", new TemperatureStats());
            _temps.TryAdd("Kunming", new TemperatureStats());
            _temps.TryAdd("Kuopio", new TemperatureStats());
            _temps.TryAdd("Kuwait City", new TemperatureStats());
            _temps.TryAdd("Kyiv", new TemperatureStats());
            _temps.TryAdd("Kyoto", new TemperatureStats());
            _temps.TryAdd("La Ceiba", new TemperatureStats());
            _temps.TryAdd("La Paz", new TemperatureStats());
            _temps.TryAdd("Lagos", new TemperatureStats());
            _temps.TryAdd("Lahore", new TemperatureStats());
            _temps.TryAdd("Lake Havasu City", new TemperatureStats());
            _temps.TryAdd("Lake Tekapo", new TemperatureStats());
            _temps.TryAdd("Las Palmas de Gran Canaria", new TemperatureStats());
            _temps.TryAdd("Las Vegas", new TemperatureStats());
            _temps.TryAdd("Launceston", new TemperatureStats());
            _temps.TryAdd("Lhasa", new TemperatureStats());
            _temps.TryAdd("Libreville", new TemperatureStats());
            _temps.TryAdd("Lisbon", new TemperatureStats());
            _temps.TryAdd("Livingstone", new TemperatureStats());
            _temps.TryAdd("Ljubljana", new TemperatureStats());
            _temps.TryAdd("Lodwar", new TemperatureStats());
            _temps.TryAdd("Lomé", new TemperatureStats());
            _temps.TryAdd("London", new TemperatureStats());
            _temps.TryAdd("Los Angeles", new TemperatureStats());
            _temps.TryAdd("Louisville", new TemperatureStats());
            _temps.TryAdd("Luanda", new TemperatureStats());
            _temps.TryAdd("Lubumbashi", new TemperatureStats());
            _temps.TryAdd("Lusaka", new TemperatureStats());
            _temps.TryAdd("Luxembourg City", new TemperatureStats());
            _temps.TryAdd("Lviv", new TemperatureStats());
            _temps.TryAdd("Lyon", new TemperatureStats());
            _temps.TryAdd("Madrid", new TemperatureStats());
            _temps.TryAdd("Mahajanga", new TemperatureStats());
            _temps.TryAdd("Makassar", new TemperatureStats());
            _temps.TryAdd("Makurdi", new TemperatureStats());
            _temps.TryAdd("Malabo", new TemperatureStats());
            _temps.TryAdd("Malé", new TemperatureStats());
            _temps.TryAdd("Managua", new TemperatureStats());
            _temps.TryAdd("Manama", new TemperatureStats());
            _temps.TryAdd("Mandalay", new TemperatureStats());
            _temps.TryAdd("Mango", new TemperatureStats());
            _temps.TryAdd("Manila", new TemperatureStats());
            _temps.TryAdd("Maputo", new TemperatureStats());
            _temps.TryAdd("Marrakesh", new TemperatureStats());
            _temps.TryAdd("Marseille", new TemperatureStats());
            _temps.TryAdd("Maun", new TemperatureStats());
            _temps.TryAdd("Medan", new TemperatureStats());
            _temps.TryAdd("Mek'ele", new TemperatureStats());
            _temps.TryAdd("Melbourne", new TemperatureStats());
            _temps.TryAdd("Memphis", new TemperatureStats());
            _temps.TryAdd("Mexicali", new TemperatureStats());
            _temps.TryAdd("Mexico City", new TemperatureStats());
            _temps.TryAdd("Miami", new TemperatureStats());
            _temps.TryAdd("Milan", new TemperatureStats());
            _temps.TryAdd("Milwaukee", new TemperatureStats());
            _temps.TryAdd("Minneapolis", new TemperatureStats());
            _temps.TryAdd("Minsk", new TemperatureStats());
            _temps.TryAdd("Mogadishu", new TemperatureStats());
            _temps.TryAdd("Mombasa", new TemperatureStats());
            _temps.TryAdd("Monaco", new TemperatureStats());
            _temps.TryAdd("Moncton", new TemperatureStats());
            _temps.TryAdd("Monterrey", new TemperatureStats());
            _temps.TryAdd("Montreal", new TemperatureStats());
            _temps.TryAdd("Moscow", new TemperatureStats());
            _temps.TryAdd("Mumbai", new TemperatureStats());
            _temps.TryAdd("Murmansk", new TemperatureStats());
            _temps.TryAdd("Muscat", new TemperatureStats());
            _temps.TryAdd("Mzuzu", new TemperatureStats());
            _temps.TryAdd("N'Djamena", new TemperatureStats());
            _temps.TryAdd("Naha", new TemperatureStats());
            _temps.TryAdd("Nairobi", new TemperatureStats());
            _temps.TryAdd("Nakhon Ratchasima", new TemperatureStats());
            _temps.TryAdd("Napier", new TemperatureStats());
            _temps.TryAdd("Napoli", new TemperatureStats());
            _temps.TryAdd("Nashville", new TemperatureStats());
            _temps.TryAdd("Nassau", new TemperatureStats());
            _temps.TryAdd("Ndola", new TemperatureStats());
            _temps.TryAdd("New Delhi", new TemperatureStats());
            _temps.TryAdd("New Orleans", new TemperatureStats());
            _temps.TryAdd("New York City", new TemperatureStats());
            _temps.TryAdd("Ngaoundéré", new TemperatureStats());
            _temps.TryAdd("Niamey", new TemperatureStats());
            _temps.TryAdd("Nicosia", new TemperatureStats());
            _temps.TryAdd("Niigata", new TemperatureStats());
            _temps.TryAdd("Nouadhibou", new TemperatureStats());
            _temps.TryAdd("Nouakchott", new TemperatureStats());
            _temps.TryAdd("Novosibirsk", new TemperatureStats());
            _temps.TryAdd("Nuuk", new TemperatureStats());
            _temps.TryAdd("Odesa", new TemperatureStats());
            _temps.TryAdd("Odienné", new TemperatureStats());
            _temps.TryAdd("Oklahoma City", new TemperatureStats());
            _temps.TryAdd("Omaha", new TemperatureStats());
            _temps.TryAdd("Oranjestad", new TemperatureStats());
            _temps.TryAdd("Oslo", new TemperatureStats());
            _temps.TryAdd("Ottawa", new TemperatureStats());
            _temps.TryAdd("Ouagadougou", new TemperatureStats());
            _temps.TryAdd("Ouahigouya", new TemperatureStats());
            _temps.TryAdd("Ouarzazate", new TemperatureStats());
            _temps.TryAdd("Oulu", new TemperatureStats());
            _temps.TryAdd("Palembang", new TemperatureStats());
            _temps.TryAdd("Palermo", new TemperatureStats());
            _temps.TryAdd("Palm Springs", new TemperatureStats());
            _temps.TryAdd("Palmerston North", new TemperatureStats());
            _temps.TryAdd("Panama City", new TemperatureStats());
            _temps.TryAdd("Parakou", new TemperatureStats());
            _temps.TryAdd("Paris", new TemperatureStats());
            _temps.TryAdd("Perth", new TemperatureStats());
            _temps.TryAdd("Petropavlovsk-Kamchatsky", new TemperatureStats());
            _temps.TryAdd("Philadelphia", new TemperatureStats());
            _temps.TryAdd("Phnom Penh", new TemperatureStats());
            _temps.TryAdd("Phoenix", new TemperatureStats());
            _temps.TryAdd("Pittsburgh", new TemperatureStats());
            _temps.TryAdd("Podgorica", new TemperatureStats());
            _temps.TryAdd("Pointe-Noire", new TemperatureStats());
            _temps.TryAdd("Pontianak", new TemperatureStats());
            _temps.TryAdd("Port Moresby", new TemperatureStats());
            _temps.TryAdd("Port Sudan", new TemperatureStats());
            _temps.TryAdd("Port Vila", new TemperatureStats());
            _temps.TryAdd("Port-Gentil", new TemperatureStats());
            _temps.TryAdd("Portland (OR)", new TemperatureStats());
            _temps.TryAdd("Porto", new TemperatureStats());
            _temps.TryAdd("Prague", new TemperatureStats());
            _temps.TryAdd("Praia", new TemperatureStats());
            _temps.TryAdd("Pretoria", new TemperatureStats());
            _temps.TryAdd("Pyongyang", new TemperatureStats());
            _temps.TryAdd("Rabat", new TemperatureStats());
            _temps.TryAdd("Rangpur", new TemperatureStats());
            _temps.TryAdd("Reggane", new TemperatureStats());
            _temps.TryAdd("Reykjavík", new TemperatureStats());
            _temps.TryAdd("Riga", new TemperatureStats());
            _temps.TryAdd("Riyadh", new TemperatureStats());
            _temps.TryAdd("Rome", new TemperatureStats());
            _temps.TryAdd("Roseau", new TemperatureStats());
            _temps.TryAdd("Rostov-on-Don", new TemperatureStats());
            _temps.TryAdd("Sacramento", new TemperatureStats());
            _temps.TryAdd("Saint Petersburg", new TemperatureStats());
            _temps.TryAdd("Saint-Pierre", new TemperatureStats());
            _temps.TryAdd("Salt Lake City", new TemperatureStats());
            _temps.TryAdd("San Antonio", new TemperatureStats());
            _temps.TryAdd("San Diego", new TemperatureStats());
            _temps.TryAdd("San Francisco", new TemperatureStats());
            _temps.TryAdd("San Jose", new TemperatureStats());
            _temps.TryAdd("San José", new TemperatureStats());
            _temps.TryAdd("San Juan", new TemperatureStats());
            _temps.TryAdd("San Salvador", new TemperatureStats());
            _temps.TryAdd("Sana'a", new TemperatureStats());
            _temps.TryAdd("Santo Domingo", new TemperatureStats());
            _temps.TryAdd("Sapporo", new TemperatureStats());
            _temps.TryAdd("Sarajevo", new TemperatureStats());
            _temps.TryAdd("Saskatoon", new TemperatureStats());
            _temps.TryAdd("Seattle", new TemperatureStats());
            _temps.TryAdd("Ségou", new TemperatureStats());
            _temps.TryAdd("Seoul", new TemperatureStats());
            _temps.TryAdd("Seville", new TemperatureStats());
            _temps.TryAdd("Shanghai", new TemperatureStats());
            _temps.TryAdd("Singapore", new TemperatureStats());
            _temps.TryAdd("Skopje", new TemperatureStats());
            _temps.TryAdd("Sochi", new TemperatureStats());
            _temps.TryAdd("Sofia", new TemperatureStats());
            _temps.TryAdd("Sokoto", new TemperatureStats());
            _temps.TryAdd("Split", new TemperatureStats());
            _temps.TryAdd("St. John's", new TemperatureStats());
            _temps.TryAdd("St. Louis", new TemperatureStats());
            _temps.TryAdd("Stockholm", new TemperatureStats());
            _temps.TryAdd("Surabaya", new TemperatureStats());
            _temps.TryAdd("Suva", new TemperatureStats());
            _temps.TryAdd("Suwałki", new TemperatureStats());
            _temps.TryAdd("Sydney", new TemperatureStats());
            _temps.TryAdd("Tabora", new TemperatureStats());
            _temps.TryAdd("Tabriz", new TemperatureStats());
            _temps.TryAdd("Taipei", new TemperatureStats());
            _temps.TryAdd("Tallinn", new TemperatureStats());
            _temps.TryAdd("Tamale", new TemperatureStats());
            _temps.TryAdd("Tamanrasset", new TemperatureStats());
            _temps.TryAdd("Tampa", new TemperatureStats());
            _temps.TryAdd("Tashkent", new TemperatureStats());
            _temps.TryAdd("Tauranga", new TemperatureStats());
            _temps.TryAdd("Tbilisi", new TemperatureStats());
            _temps.TryAdd("Tegucigalpa", new TemperatureStats());
            _temps.TryAdd("Tehran", new TemperatureStats());
            _temps.TryAdd("Tel Aviv", new TemperatureStats());
            _temps.TryAdd("Thessaloniki", new TemperatureStats());
            _temps.TryAdd("Thiès", new TemperatureStats());
            _temps.TryAdd("Tijuana", new TemperatureStats());
            _temps.TryAdd("Timbuktu", new TemperatureStats());
            _temps.TryAdd("Tirana", new TemperatureStats());
            _temps.TryAdd("Toamasina", new TemperatureStats());
            _temps.TryAdd("Tokyo", new TemperatureStats());
            _temps.TryAdd("Toliara", new TemperatureStats());
            _temps.TryAdd("Toluca", new TemperatureStats());
            _temps.TryAdd("Toronto", new TemperatureStats());
            _temps.TryAdd("Tripoli", new TemperatureStats());
            _temps.TryAdd("Tromsø", new TemperatureStats());
            _temps.TryAdd("Tucson", new TemperatureStats());
            _temps.TryAdd("Tunis", new TemperatureStats());
            _temps.TryAdd("Ulaanbaatar", new TemperatureStats());
            _temps.TryAdd("Upington", new TemperatureStats());
            _temps.TryAdd("Ürümqi", new TemperatureStats());
            _temps.TryAdd("Vaduz", new TemperatureStats());
            _temps.TryAdd("Valencia", new TemperatureStats());
            _temps.TryAdd("Valletta", new TemperatureStats());
            _temps.TryAdd("Vancouver", new TemperatureStats());
            _temps.TryAdd("Veracruz", new TemperatureStats());
            _temps.TryAdd("Vienna", new TemperatureStats());
            _temps.TryAdd("Vientiane", new TemperatureStats());
            _temps.TryAdd("Villahermosa", new TemperatureStats());
            _temps.TryAdd("Vilnius", new TemperatureStats());
            _temps.TryAdd("Virginia Beach", new TemperatureStats());
            _temps.TryAdd("Vladivostok", new TemperatureStats());
            _temps.TryAdd("Warsaw", new TemperatureStats());
            _temps.TryAdd("Washington, D.C.", new TemperatureStats());
            _temps.TryAdd("Wau", new TemperatureStats());
            _temps.TryAdd("Wellington", new TemperatureStats());
            _temps.TryAdd("Whitehorse", new TemperatureStats());
            _temps.TryAdd("Wichita", new TemperatureStats());
            _temps.TryAdd("Willemstad", new TemperatureStats());
            _temps.TryAdd("Winnipeg", new TemperatureStats());
            _temps.TryAdd("Wrocław", new TemperatureStats());
            _temps.TryAdd("Xi'an", new TemperatureStats());
            _temps.TryAdd("Yakutsk", new TemperatureStats());
            _temps.TryAdd("Yangon", new TemperatureStats());
            _temps.TryAdd("Yaoundé", new TemperatureStats());
            _temps.TryAdd("Yellowknife", new TemperatureStats());
            _temps.TryAdd("Yerevan", new TemperatureStats());
            _temps.TryAdd("Yinchuan", new TemperatureStats());
            _temps.TryAdd("Zagreb", new TemperatureStats());
            _temps.TryAdd("Zanzibar City", new TemperatureStats());
            _temps.TryAdd("Zürich", new TemperatureStats());
        }
    }
}
