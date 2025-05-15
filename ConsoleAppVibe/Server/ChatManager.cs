using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using ConsoleAppVibe.Models;
using ConsoleAppVibe.DTO;

namespace ConsoleAppVibe.Server
{
    public class ChatManager
    {
        private const int Port = 5000;
        private TcpListener _server;

        private static readonly ConcurrentDictionary<int, TcpClient> ConnectedClients = new ConcurrentDictionary<int, TcpClient>();
        private static readonly ConcurrentDictionary<int, int> ClientChatMapping = new ConcurrentDictionary<int, int>();

        public static void Main(string[] args)
        {
            var server = new ChatManager();
            server.StartServer();
        }

        public void StartServer()
        {
            _server = new TcpListener(IPAddress.Any, Port);
            _server.Start();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [SERVER] Listening on port {Port}...");

            while (true)
            {
                TcpClient client = _server.AcceptTcpClient();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [SERVER] Client connected.");

                Thread clientThread = new Thread(HandleClient);
                clientThread.Start(client);
            }
        }

        private void HandleClient(object obj)
        {
            var client = (TcpClient)obj;
            var stream = client.GetStream();
            var buffer = new byte[1024];
            int byteCount;
            int currentUserId = -1;
            int currentChatId = -1;

            try
            {
                byteCount = stream.Read(buffer, 0, buffer.Length);
                var initJson = Encoding.UTF8.GetString(buffer, 0, byteCount);
                var initData = JsonConvert.DeserializeObject<SocketsDTO>(initJson);

                if (initData == null || initData.sender_id <= 0 || initData.chat_id <= 0)
                {
                    SendResponse(stream, "Invalid connection data");
                    return;
                }

                using (var db = new VibeManagerEntities())
                {
                    if (!db.USERS.Any(u => u.id == initData.sender_id) || !db.CHAT.Any(c => c.id == initData.chat_id))
                    {
                        SendResponse(stream, "Invalid user or chat");
                        return;
                    }
                }

                currentUserId = initData.sender_id;
                currentChatId = initData.chat_id;

                ConnectedClients[currentUserId] = client;
                ClientChatMapping[currentUserId] = currentChatId;

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [INFO] User {currentUserId} joined chat {currentChatId}.");

                using (var db = new VibeManagerEntities())
                {
                    var messages = db.MESSAGES
                                     .Where(m => m.id_chat == currentChatId)
                                     .OrderBy(m => m.send_at)
                                     .ToList();

                    foreach (var message in messages)
                    {
                        var responseMessage = JsonConvert.SerializeObject(new
                        {
                            type = "message",
                            message_id = message.id,
                            from = message.sender_id,
                            content = message.context,
                            timestamp = message.send_at.ToString("o")
                        });

                        SendResponse(stream, responseMessage);
                    }
                }

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        Console.WriteLine($"[RECEIVED] {line}");
                        var data = JsonConvert.DeserializeObject<SocketsDTO>(line);

                        // 🔁 IGNORAR mensajes de ping
                        if (data?.content?.Trim() == "ping")
                        {
                            Console.WriteLine($"[PING] Received ping from user {currentUserId}");
                            continue; // No guardar ni reenviar
                        }

                        if (data == null || data.sender_id != currentUserId || data.chat_id != currentChatId || string.IsNullOrWhiteSpace(data.content))
                        {
                            Console.WriteLine("[SECURITY] Invalid message structure or spoofing attempt.");
                            continue;
                        }

                        using (var db = new VibeManagerEntities())
                        {
                            var newMessage = new MESSAGES
                            {
                                sender_id = currentUserId,
                                context = data.content,
                                send_at = DateTime.Now,
                                id_chat = currentChatId
                            };
                            db.MESSAGES.Add(newMessage);
                            db.SaveChanges();

                            var payload = JsonConvert.SerializeObject(new
                            {
                                type = "message",
                                message_id = newMessage.id,
                                from = newMessage.sender_id,
                                content = newMessage.context,
                                timestamp = newMessage.send_at.ToString("o")
                            });

                            foreach (var kvp in ConnectedClients)
                            {
                                if (ClientChatMapping.TryGetValue(kvp.Key, out int userChatId) && userChatId == currentChatId)
                                {
                                    try
                                    {
                                        SendResponse(kvp.Value.GetStream(), payload);
                                    }
                                    catch
                                    {
                                        Console.WriteLine($"[ERROR] Could not send message to user {kvp.Key}");
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] {ex.Message}");
            }
            finally
            {
                if (currentUserId > 0)
                {
                    ConnectedClients.TryRemove(currentUserId, out _);
                    ClientChatMapping.TryRemove(currentUserId, out _);
                    Console.WriteLine($"[INFO] User {currentUserId} disconnected");
                }

                client.Close();
            }
        }

        private void SendResponse(NetworkStream stream, string message)
        {
            var response = Encoding.UTF8.GetBytes(message);
            stream.Write(response, 0, response.Length);
            stream.WriteByte((byte)'\n');
        }
    }
}
