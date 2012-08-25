using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Vosen.MAL.Content
{
    public class AnimeResult
    {
        public AnimeResponse Response { get; internal set; }
        public string RomajiName { get; internal set; }
        public string EnglishName { get; internal set; }
        public IList<string> Synonyms { get; internal set; }
    }
}
