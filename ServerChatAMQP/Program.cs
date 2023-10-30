using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Threading;
using Serilog;

namespace ServerChatAMQP
{
    class Program
    {
        // Enum to represent agent levels
        public enum AgentLevel
        {
            Junior = 0,
            Mid = 1,
            Senior = 2,
            Lead = 3
        }

        public enum Shift
        {
            Shift1 = 1,
            Shift2 = 2,
            Shift3 = 3
        }

        static string FindAgentIdByCustomer(Dictionary<string, Dictionary<string, string>> dataDictionary, string customerId)
        {
            foreach (var agentData in dataDictionary.Values)
            {
                string assignedCustomers = agentData["AssignedCustomerId"];
                string[] customers = assignedCustomers.Split(',');

                if (Array.Exists(customers, c => c == customerId))
                {
                    return agentData["AgentId"];
                }
            }

            return null; // Customer not found
        }        

        static void AllocateCustomerByLevel(Dictionary<string, Dictionary<string, string>> dataDictionary, string customerId, AgentLevel desiredLevel)
        {
            foreach (var agentId in dataDictionary.Keys)
            {
                var agentData = dataDictionary[agentId];
                int agentLevel = int.Parse(agentData["Level"]);
                
                // Check if agent level matches the desired level and if the agent can handle more customers
                if (agentLevel >= (int)desiredLevel && agentData["AssignedCustomerId"].Split(',').Length < 2)
                {
                    //agentData["AssignedCustomerId"] += string.Format(",{0}", customerId);
                    agentData["AssignedCustomerId"] += customerId;
                    break;
                }
            }
        }

        static void AllocateCustomerByLevelAndShift(Dictionary<string, Dictionary<string, string>> dataDictionary, string customerId)
        {
            int levelCount = Enum.GetValues(typeof(AgentLevel)).Length;
            int shiftCount = Enum.GetValues(typeof(Shift)).Length;
            int currentLevel = 0;
            int currentShift = GetCurrentShift();
            
            while (true)
            {
                foreach (var agentData in dataDictionary.Values)
                {
                    int agentLevel = int.Parse(agentData["Level"]);
                    Shift agentShift = (Shift)Enum.Parse(typeof(Shift), agentData["Shift"]);
                    
                    if (agentLevel == currentLevel && agentShift == (Shift)currentShift &&
                        agentData["Available"].Equals("Yes", StringComparison.OrdinalIgnoreCase) &&
                        agentData["AssignedCustomerId"].Split(',').Length < 2)
                    {
                        agentData["AssignedCustomerId"] += customerId;
                        return;
                    }
                }
                currentShift = (currentShift % shiftCount) + 1; // Move to the next shift in round-robin fashion
                if (currentShift == 1)
                {
                    currentLevel = (currentLevel + 1) % levelCount; // Move to the next level when starting a new day (shift 1)
                }
            }
        }

        static void UpdateAgentAvailability(Dictionary<string, Dictionary<string, string>> dataDictionary, string agentId, string availability)
        {
            if (dataDictionary.ContainsKey(agentId))
            {
                dataDictionary[agentId]["Available"] = availability;
            }
        }

        static void ClearAssignedCustomer(Dictionary<string, Dictionary<string, string>> dataDictionary, string agentId)
        {
            if (dataDictionary.ContainsKey(agentId))
            {
                dataDictionary[agentId]["AssignedCustomerId"] = "";
            }
        }

        static int GetCurrentShift()
        {
            DateTime currentTime = DateTime.Now;
            int currentHour = currentTime.Hour;

            if (currentHour >= 9 && currentHour <= 13)
            {
                return 1;
            }
            else if (currentHour >= 13 && currentHour <= 15)
            {
                return 2;
            }
            else if (currentHour >= 16 && currentHour <= 19)
            {
                return 3;
            }

            return 0; // Outside office hours
        }

        static double CalculateConcurrentCapacity(Dictionary<string, Dictionary<string, string>> dataDictionary, int currentShift)
        {
            int maxChatHandles = 10;
            double totalConcurrentCapacity = 0.0;

            foreach (var agentData in dataDictionary.Values)
            {
                int agentLevel = int.Parse(agentData["Level"]);
                int agentShift = int.Parse(agentData["Shift"]);

                // Check if the agent is in the current shift
                if (agentShift == currentShift)
                {
                    double seniorityMultiplier = GetSeniorityMultiplier((AgentLevel)agentLevel);

                    // Calculate concurrent chat capacity for this agent
                    double concurrentCapacity = maxChatHandles * seniorityMultiplier;

                    // Add this agent's capacity to the total concurrent capacity
                    totalConcurrentCapacity += concurrentCapacity;
                }
            }

            return totalConcurrentCapacity;
        }


        static double CalculateConcurrentCapacity(Dictionary<string, Dictionary<string, string>> dataDictionary)
        {
            int maxChatHandles = 10;
            double totalConcurrentCapacity = 0.0;

            foreach (var agentData in dataDictionary.Values)
            {
                int agentLevel = int.Parse(agentData["Level"]);
                double seniorityMultiplier = GetSeniorityMultiplier((AgentLevel)agentLevel);

                // Calculate concurrent chat capacity for this agent
                double concurrentCapacity = maxChatHandles * seniorityMultiplier;

                // Add this agent's capacity to the total concurrent capacity
                totalConcurrentCapacity += concurrentCapacity;
            }

            return totalConcurrentCapacity;
        }

        static double GetSeniorityMultiplier(AgentLevel level)
        {
            switch (level)
            {
                case AgentLevel.Junior:
                    return 0.4;
                case AgentLevel.Mid:
                    return 0.6;
                case AgentLevel.Senior:
                    return 0.8;
                case AgentLevel.Lead:
                    return 0.5;
                default:
                    return 0.0;
            }
        }

        static void Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration().WriteTo.Console().WriteTo.File("server.log", rollingInterval: RollingInterval.Day).CreateLogger();
            Log.Information("Server started.");
            Console.Title = "Server";
            Console.ForegroundColor = ConsoleColor.White;
            try
            {
                // Maximum queue size
                int maxQueueSize = 10; // Adjust this number according to your requirement

                string csvFilePath = "team.csv"; // Provide the path to your CSV file
                Dictionary<string, Dictionary<string, string>> dataDictionary = CsvReaderHelper.ReadCsvFile(csvFilePath);

                // Create an instance of SessionMonitor
                SessionMonitor sessionMonitor = new SessionMonitor();

                var factory = new ConnectionFactory()
                {
                    HostName = "localhost", // RabbitMQ server address
                    UserName = "guest",
                    Password = "guest"
                };

                var connection = factory.CreateConnection();
                var customersChannel = connection.CreateModel();
                var agentsChannel = connection.CreateModel();

                // Queue for receiving messages from customers
                customersChannel.QueueDeclare(queue: "customer_queue", durable: false, exclusive: false, autoDelete: false, arguments: null);
                // Queue for forwarding messages to agents
                agentsChannel.QueueDeclare(queue: "agent_queue", durable: false, exclusive: false, autoDelete: false, arguments: null);

                //Listening the response from customers
                var customerConsumer = new EventingBasicConsumer(customersChannel);
                customerConsumer.Received += (model, ea) =>
                {
                    var body = ea.Body.ToArray();
                    var message = Encoding.UTF8.GetString(body);
                    // Logic to find available agents and allocate an agent
                    // For simplicity, let's assume there's only one agent available
                    // For simplicity, let's assume the Customer's ID is included in the message
                    var customerId = message.Split(':')[0]; // Extract Customer ID from the message

                    if (GetCurrentShift() != 0)
                    {
                        double ConcurrentCapacity = CalculateConcurrentCapacity(dataDictionary, GetCurrentShift());
                        Console.WriteLine("Concurrent Capacity {0}", ConcurrentCapacity);
                        if (sessionMonitor.GetQueueSize() <= ConcurrentCapacity);
                        {
                            // Check the queue size before accepting a new chat session FIFO
                            if (sessionMonitor.GetQueueSize() < maxQueueSize)
                            {
                                //MONITORING The Session
                                var session = new ChatSession
                                {
                                    SessionId = Guid.NewGuid().ToString(),
                                    CustomerId = customerId,
                                    AgentId = FindAgentIdByCustomer(dataDictionary, customerId),
                                    CreationTime = DateTime.Now,
                                    LastActiveTime = DateTime.Now,
                                    IsActive = true
                                };

                                // Enqueue the session into the SessionMonitor
                                sessionMonitor.EnqueueSession(session);
                                // Check if customerId exists in the dictionary
                                string agentId = FindAgentIdByCustomer(dataDictionary, customerId);
                                if (agentId == null)
                                {
                                    //AllocateCustomerByLevel(dataDictionary, customerId, AgentLevel.Junior);
                                    AllocateCustomerByLevelAndShift(dataDictionary, customerId);
                                    agentId = FindAgentIdByCustomer(dataDictionary, customerId);
                                }
                                // Forward message to the allocated agent
                                var agentResponse = string.Format("{0}:{1}", message.Split(':')[0], message.Substring(message.IndexOf(':') + 1));
                                var agentMessageBody = Encoding.UTF8.GetBytes(agentResponse);
                                agentsChannel.BasicPublish(exchange: "", routingKey: "agent_response_queue", basicProperties: null, body: agentMessageBody);
                                Console.WriteLine(string.Format("{0}:{1}", message.Split(':')[0], message.Substring(message.IndexOf(':') + 1)));
                            }
                        }
                        //else{
                            
                        //    var serverResponse = "Server:Chat queue is overflow";
                        //    var serverMessageBody = Encoding.UTF8.GetBytes(serverResponse);
                        //    agentsChannel.BasicPublish(exchange: "", routingKey: "customer_response_queue", basicProperties: null, body: serverMessageBody);
                        //    Console.WriteLine(string.Format("{0}:{1}", message.Split(':')[0], message.Substring(message.IndexOf(':') + 1)));
                        //}
                    }
                    else
                    {
                        var serverResponse = "Server:Chat is offline";
                        var serverMessageBody = Encoding.UTF8.GetBytes(serverResponse);
                        agentsChannel.BasicPublish(exchange: "", routingKey: "customer_response_queue", basicProperties: null, body: serverMessageBody);
                        Console.WriteLine(string.Format("{0}:{1}", message.Split(':')[0], message.Substring(message.IndexOf(':') + 1)));
                    }
                };

                // Listen for responses from agents
                var agentConsumer = new EventingBasicConsumer(agentsChannel);
                agentConsumer.Received += (model, ea) =>
                {
                    var body = ea.Body.ToArray();
                    var message = Encoding.UTF8.GetString(body);
                    // Logic to determine the respective Agent based on the message
                    // For simplicity, let's assume the Agent's ID is included in the message
                    var agentId = message.Split(':')[0]; // Extract Agent ID from the message
                    if (dataDictionary.ContainsKey(agentId))
                    {
                        //Check the Shift Time Which is not equal to office closed
                        if (GetCurrentShift() != 0)
                        {
                            //Check the Shift Time is passed for current agent or not
                            if (int.Parse(dataDictionary[agentId]["Shift"]) == GetCurrentShift())
                            {
                                switch (message.Substring(message.IndexOf(':') + 1))
                                {
                                    case "online":
                                        UpdateAgentAvailability(dataDictionary, agentId, "Yes");
                                        break;
                                    case "offline":
                                        UpdateAgentAvailability(dataDictionary, agentId, "No");
                                        break;
                                    case "close":
                                        ClearAssignedCustomer(dataDictionary, agentId);
                                        break;
                                    default:
                                        break;
                                }
                                // Check if the message is from an assigned customer
                                //var customerId = message.Split(':')[2]; // Extract Customer ID from the message
                                //Console.WriteLine(customerId);
                                var customerResponse = string.Format("{0}:{1}", message.Split(':')[0], message.Substring(message.IndexOf(':') + 1));
                                //var customerResponse = string.Format("{0}:{1}", message.Split(':')[0], message.Substring(message.IndexOf(':') + 1));
                                // Forward response back to the respective customer
                                var customerResponseBody = Encoding.UTF8.GetBytes(customerResponse);
                                customersChannel.BasicPublish(exchange: "", routingKey: "customer_response_queue", basicProperties: null, body: customerResponseBody);
                                Console.WriteLine(string.Format("{0}:{1}", message.Split(':')[0], message.Substring(message.IndexOf(':') + 1)));
                            }
                            else
                            {
                                //Reset the agent activtites for shift closed 
                                ClearAssignedCustomer(dataDictionary, agentId);
                                UpdateAgentAvailability(dataDictionary, agentId, "No");
                            }
                        }
                        else
                        {
                            var serverResponse = "Server:Chat is offline";
                            var serverMessageBody = Encoding.UTF8.GetBytes(serverResponse);
                            agentsChannel.BasicPublish(exchange: "", routingKey: "agent_response_queue", basicProperties: null, body: serverMessageBody);
                            Console.WriteLine(string.Format("{0}:{1}", message.Split(':')[0], message.Substring(message.IndexOf(':') + 1)));
                        }
                    }
                    else
                    {
                        var serverResponse = "Server:Invalid Agent ID";
                        var serverMessageBody = Encoding.UTF8.GetBytes(serverResponse);
                        agentsChannel.BasicPublish(exchange: "", routingKey: "agent_response_queue", basicProperties: null, body: serverMessageBody);
                        Console.WriteLine(string.Format("{0}:{1}", message.Split(':')[0], message.Substring(message.IndexOf(':') + 1)));
                    }
                };

                // Start listening for messages from customers
                customersChannel.BasicConsume(queue: "customer_queue", autoAck: true, consumer: customerConsumer);
                // Start listening for messages from agents
                agentsChannel.BasicConsume(queue: "agent_queue", autoAck: true, consumer: agentConsumer);
                Console.WriteLine("Server is running & waiting for messages. Press [Enter] to exit.");
                Console.ReadLine();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An error occurred: {ErrorMessage}", ex.Message);
            }            
        }
    }

    //FIFO AND MONITORING

    class ChatSession
    {
        public string SessionId { get; set; }
        public string CustomerId { get; set; }
        public string AgentId { get; set; }
        public DateTime CreationTime { get; set; }
        public DateTime LastActiveTime { get; set; }
        public bool IsActive { get; set; }
    }

    class SessionMonitor
    {
        private Queue<ChatSession> sessionQueue = new Queue<ChatSession>();
        private Timer sessionMonitorTimer;

        public SessionMonitor()
        {
            // Initialize the timer to run the session monitoring logic every minute (adjust as needed)
            sessionMonitorTimer = new Timer(MonitorSessions, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        }

        public void EnqueueSession(ChatSession session)
        {
            sessionQueue.Enqueue(session);
        }

        public int GetQueueSize()
        {
            return sessionQueue.Count;
        }

        private void MonitorSessions(object state)
        {
            var currentTime = DateTime.Now;
            var inactiveThreshold = TimeSpan.FromMinutes(15); // Sessions inactive for 15 minutes are marked as inactive

            while (sessionQueue.Count > 0)
            {
                var session = sessionQueue.Peek();
                if (currentTime - session.LastActiveTime > inactiveThreshold)
                {
                    session.IsActive = false;
                    sessionQueue.Dequeue(); // Remove the session from the queue as it is inactive
                }
                else
                {
                    break; // Stop checking further sessions as they are all active (FIFO order)
                }
            }
        }
    }
}
