using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleAppVibe.DTO
{
    public class SocketsDTO
    {
        public string type { get; set; }
        public int sender_id { get; set; }
        public int chat_id { get; set; }
        public string content { get; set; }
    }

}
