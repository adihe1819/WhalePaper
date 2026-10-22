using System.Collections.Generic;
using System.Runtime.Serialization;

namespace WhalePaper.Models
{
    [DataContract]
    public sealed class LibraryData
    {
        [DataMember]
        public List<WallpaperItem> Items { get; set; }

        public LibraryData()
        {
            Items = new List<WallpaperItem>();
        }
    }
}
