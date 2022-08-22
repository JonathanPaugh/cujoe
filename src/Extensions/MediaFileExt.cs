using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FFmpeg.NET;

namespace Cujoe
{
    public static class MediaFileExt
    {
        public static string Label(this MediaFile media) => $"{media.FileInfo.Directory.Name}{Path.DirectorySeparatorChar}{media.FileInfo.Name}";
    }
}
