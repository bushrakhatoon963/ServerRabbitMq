using CsvHelper;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ServerChatAMQP
{
    class CsvReaderHelper
    {
        public static Dictionary<string, Dictionary<string, string>> ReadCsvFile(string csvFilePath)
        {
            using (var reader = new StreamReader(csvFilePath))
            using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
            {
                var records = csv.GetRecords<dynamic>();

                Dictionary<string, Dictionary<string, string>> dataDictionary = new Dictionary<string, Dictionary<string, string>>();

                foreach (var record in records)
                {
                    string agentId = record.AgentId;
                    string level = record.Level;
                    string shift = record.Shift;
                    string available = record.Available;
                    string assignedCustomerId = record.AssignedCustomerId;

                    Dictionary<string, string> agentData = new Dictionary<string, string>
                {
                    { "AgentId", agentId },
                    { "Level", level },
                    { "Shift", shift },
                    { "Available", available },
                    { "AssignedCustomerId", assignedCustomerId }
                };

                    // Assuming 'AgentId' is a unique key for each record
                    dataDictionary[agentId] = agentData;
                }

                return dataDictionary;
            }
        }
    }
}
