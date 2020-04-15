using System;

namespace DatingApp.Api.Models
{
    public class Photo
    {
        public int Id { get; set; }

        public string Url { get; set; }

        public string Description { get; set; }

        public DateTime DateAdd { get; set; }

        public bool IsMain { get; set; }

        public string PublicId { get; set; }

        public bool IsApproved { get; set; }

        public virtual User User { get; set; }

        public int UserId { get; set; }

        public Photo()
        {
            DateAdd = DateTime.Now;
        }
        
    }
}