using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HtmlAgilityPack;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Threading.Tasks;
using System.IO;

namespace Vosen.MAL.Content
{
    public static class Extract
    {
        private static Regex extractName = new Regex("myanimelist.net/profile/(.+?)\"", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static Regex trimWhitespace = new Regex(@"\s+", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static Regex captureRating = new Regex(@"http://myanimelist\.net/anime/([0-9]+?)/", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        
        public static AnimeResult DownloadAnimeNames(int id)
        {
            return AnimeNamesFromSite(GetStringFrom("http://myanimelist.net/anime/" + id));
        }

        public static AnimeResult AnimeNamesFromSite(string site)
        {
            if (site.Contains("Invalid Request"))
                return new AnimeResult() { Response = AnimeResponse.InvalidId };
            HtmlDocument doc = new HtmlDocument() { OptionCheckSyntax = false };
            doc.LoadHtml(site);
            HtmlNode contentWrapperNode = doc.GetElementbyId("contentWrapper");
            if (contentWrapperNode == null)
                return new AnimeResult() { Response = AnimeResponse.Unknown };
            string romajiName = GetMainNameFromContent(contentWrapperNode);
            Tuple<string, string[]> alternatives = ExtractAlternativeNamesFromContent(doc);
            return new AnimeResult() { Response = AnimeResponse.Successs, RomajiName = romajiName, EnglishName = alternatives.Item1, Synonyms = alternatives.Item2 };
        }

        private static Tuple<string, string[]> ExtractAlternativeNamesFromContent(HtmlDocument doc)
        {
            string englishName = null;
            string[] synonyms = new string [] { };
            HtmlNode editdiv = doc.GetElementbyId("editdiv");
            HtmlNode englishNode = editdiv.NextSibling;
            while (englishNode!= null && (!englishNode.HasAttributes || englishNode.Attributes["class"].Value != "spaceit_pad"))
                englishNode = englishNode.NextSibling;
            if (englishNode == null)
                return Tuple.Create(englishName, synonyms);
            if(englishNode.FirstChild.InnerText.ToUpperInvariant().Contains("ENGLISH"))
                englishName = englishNode.ChildNodes[1].InnerText.Trim();
            HtmlNode synonymsNode = englishNode.NextSibling;
            while (synonymsNode != null && (!synonymsNode.HasAttributes || synonymsNode.Attributes["class"].Value != "spaceit_pad"))
                synonymsNode = synonymsNode.NextSibling;
            if(synonymsNode == null)
                return Tuple.Create(englishName, synonyms);
            if(synonymsNode.ChildNodes.Count >= 2 && synonymsNode.FirstChild.InnerText.ToUpperInvariant().Contains("SYNONYMS"))
                synonyms = synonymsNode.ChildNodes[1].InnerText.Split(new string[] {","}, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
            return Tuple.Create(englishName, synonyms);
        }

        private static string GetMainNameFromContent(HtmlNode contentWrapperNode)
        {
            HtmlNode h1Node = contentWrapperNode.ChildNodes.FindFirst("h1");
            return h1Node.LastChild.InnerText;
        }

        private static string GetStringFrom(string url)
        {
            var helper = new HttpHelper(url);
            helper.HttpWebRequest.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;
            helper.HttpWebRequest.Proxy = null;
            helper.HttpWebRequest.ServicePoint.ConnectionLimit = Int32.MaxValue;
            helper.HttpWebRequest.ContentType = "text/html; charset=utf-8";
            using (var stream = helper.OpenRead())
            {
                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static Task<string> GetStringFromAsync(string url)
        {
            var helper = new HttpHelper(url);
            helper.HttpWebRequest.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;
            helper.HttpWebRequest.Proxy = null;
            helper.HttpWebRequest.ServicePoint.ConnectionLimit = Int32.MaxValue;
            helper.HttpWebRequest.ContentType = "text/html; charset=utf-8";
            var streamTask = helper.OpenReadTaskAsync();
            return streamTask.ContinueWith((asc) =>
            {
                using (var reader = new StreamReader(asc.Result))
                {
                    string result = reader.ReadToEnd();
                    asc.Result.Dispose();
                    return result;
                }
            });
        }

        public static NameResult DownloadName(int id)
        {
            string site;
            var helper = new HttpHelper(@"http://myanimelist.net/showclubs.php?id=" + id);
            helper.HttpWebRequest.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;
            helper.HttpWebRequest.Proxy = null;
            helper.HttpWebRequest.ServicePoint.ConnectionLimit = Int32.MaxValue;
            using (var stream = helper.OpenRead())
            {
                using (var reader = new StreamReader(stream))
                {
                    return NameFromClublist(reader.ReadToEnd());
                }
            }
        }

        public static NameResult NameFromClublist(string site)
        {
            if (site.Contains("<h1>Invalid</h1>"))
                return new NameResult() { Response = NameResponse.InvalidId };
            Match match = extractName.Match(site);
            if (match.Success)
                return new NameResult() { Response = NameResponse.Success, Name = match.Groups[1].Value };
            return new NameResult() { Response = NameResponse.Unknown };
        }

        public static AnimelistResult DownloadRatedAnime(string name)
        {
            string site =GetStringFrom("http://myanimelist.net/animelist/" + name + "&status=7");
            var ratingTuple = ExtractionCore(site);
            if (ratingTuple.Item1 == AnimelistResponse.TooLarge)
                return DownloadRatedAnimeFromSublists(name);
            return RatedAnimeCore(ratingTuple);
        }

        private static AnimelistResult DownloadRatedAnimeFromSublists(string name)
        {
            Task<string> watchingTask = GetStringFromAsync("http://myanimelist.net/animelist/" + name + "&status=1");
            Task<string> completedTask = GetStringFromAsync("http://myanimelist.net/animelist/" + name + "&status=2");
            Task<string> onHoldTask = GetStringFromAsync("http://myanimelist.net/animelist/" + name + "&status=3");
            Task<string> droppedTask = GetStringFromAsync("http://myanimelist.net/animelist/" + name + "&status=4");
            Task<string>[] tasks = new Task<string>[] { watchingTask, completedTask, onHoldTask, droppedTask };
            Task.WaitAll(tasks);
            watchingTask.Dispose();
            completedTask.Dispose();
            onHoldTask.Dispose();
            droppedTask.Dispose();
            var allRatedAnime = tasks.Select(task => RatedAnime(task.Result))
                                     .Where(result => result.Response == AnimelistResponse.Successs)
                                     .SelectMany(i => i.Ratings);
            return new AnimelistResult() { Response = AnimelistResponse.Successs, Ratings = allRatedAnime.ToList() };
        }

        public static AnimelistResult RatedAnime(string site)
        {
            Tuple<AnimelistResponse, IEnumerable<AnimeRating>> parseResult = ExtractionCore(site);
            return RatedAnimeCore(parseResult);
        }

        private static AnimelistResult RatedAnimeCore(Tuple<AnimelistResponse, IEnumerable<AnimeRating>> parseResult)
        {
            if (parseResult.Item1 == AnimelistResponse.Successs)
                return new AnimelistResult() { Response = parseResult.Item1, Ratings = parseResult.Item2.Where(anime => anime.rating > 0).ToList() };
            return new AnimelistResult() { Response = parseResult.Item1 };
        }

        public static AnimelistResult DownloadAlldAnime(string name)
        {
            string site= GetStringFrom("http://myanimelist.net/animelist/" + name + "&status=7");
            var ratingTuple = ExtractionCore(site);
            if (ratingTuple.Item1 == AnimelistResponse.TooLarge)
                return DownloadAllAnimeFromSublists(name);
            return AllAnimeCore(ratingTuple);
        }

        private static AnimelistResult DownloadAllAnimeFromSublists(string name)
        {
            Task<string> watchingTask = GetStringFromAsync("http://myanimelist.net/animelist/" + name + "&status=1");
            Task<string> completedTask = GetStringFromAsync("http://myanimelist.net/animelist/" + name + "&status=2");
            Task<string> onHoldTask = GetStringFromAsync("http://myanimelist.net/animelist/" + name + "&status=3");
            Task<string> droppedTask = GetStringFromAsync("http://myanimelist.net/animelist/" + name + "&status=4");
            Task<string>[] tasks = new Task<string>[] { watchingTask, completedTask, onHoldTask, droppedTask };
            Task.WaitAll(tasks);
            watchingTask.Dispose();
            completedTask.Dispose();
            onHoldTask.Dispose();
            droppedTask.Dispose();
            var allAnime = tasks.Select(task => AllAnime(task.Result))
                                .Where(result => result.Response == AnimelistResponse.Successs)
                                .SelectMany(i => i.Ratings);
            return new AnimelistResult() { Response = AnimelistResponse.Successs, Ratings = allAnime.ToList() };
        }

        public static AnimelistResult AllAnime(string site)
        {
            Tuple<AnimelistResponse, IEnumerable<AnimeRating>> parseResult = ExtractionCore(site);
            return AllAnimeCore(parseResult);
        }

        private static AnimelistResult AllAnimeCore(Tuple<AnimelistResponse, IEnumerable<AnimeRating>> parseResult)
        {
            if (parseResult.Item1 == AnimelistResponse.Successs)
                return new AnimelistResult() { Response = parseResult.Item1, Ratings = parseResult.Item2.ToList() };
            return new AnimelistResult() { Response = parseResult.Item1 };
        }

        private static Tuple<AnimelistResponse, IEnumerable<AnimeRating>> ExtractionCore(string site)
        {
            if (site.Contains("There was a MySQL Error."))
            {
                return new Tuple<AnimelistResponse, IEnumerable<AnimeRating>>(AnimelistResponse.MySQLError, null);
            }

            if (site.Contains("Invalid Username Supplied"))
            {
                return new Tuple<AnimelistResponse, IEnumerable<AnimeRating>>(AnimelistResponse.InvalidUsername, null);
            }

            if (site.Contains("This list has been made private by the owner."))
            {
                return new Tuple<AnimelistResponse, IEnumerable<AnimeRating>>(AnimelistResponse.ListIsPrivate, null);
            }

            if (site.Contains("\"All Anime\" is disabled for lists with greater than 1500 anime entries."))
            {
                return new Tuple<AnimelistResponse, IEnumerable<AnimeRating>>(AnimelistResponse.TooLarge, null);
            }

            HtmlDocument doc = new HtmlDocument() { OptionCheckSyntax = false };
            doc.LoadHtml(site);
            HtmlNode tableNode = doc.GetElementbyId("list_surround");
            if (tableNode == null)
                return new Tuple<AnimelistResponse, IEnumerable<AnimeRating>>(AnimelistResponse.Unknown, null);
            var mainIndices = FindTitleRatingIndices(tableNode);
            // check for people who don't put ratings on their profiles
            if (mainIndices == null)
            {
                return new Tuple<AnimelistResponse, IEnumerable<AnimeRating>>(AnimelistResponse.Successs, new AnimeRating[0]);
            }
            // collect ratings
            var ratings = tableNode.ChildNodes
                .TakeWhile(n => !(n.Attributes.Contains("class") && n.Attributes["class"].Value == "header_ptw"))
                .Where(n => n.Name == "table" && !n.Attributes.Contains("class"))
                .Select(n => ExtractPayload(n, mainIndices.Item1, mainIndices.Item2))
                .Where(t => t != null)
                .Select(t => ParseRatings(t.Item1, t.Item2));
            // return our findings
            return new Tuple<AnimelistResponse, IEnumerable<AnimeRating>>(AnimelistResponse.Successs, ratings);
        }

        private static Tuple<int, int> FindTitleRatingIndices(HtmlNode outerNode)
        {
            var headNodes = outerNode.ChildNodes.Select(ExtractHeadCells).FirstOrDefault(e => e != null);
            if (headNodes == null)
                return null;
            int title = -1;
            int rating = -1;
            // we've got <td> nodes containing titles
            for (int i = 0; i < headNodes.Count; i++)
            {
                // strip whitespace
                string innerText = trimWhitespace.Replace(headNodes[i].InnerText, " ");
                if (innerText == "Anime Title")
                {
                    title = i;
                }
                else
                {
                    // look for a <strong> child
                    var strongNode = headNodes[i].ChildNodes.FirstOrDefault(n => n.Name == "strong");
                    if (strongNode != null && strongNode.InnerText == "Score")
                        rating = i;
                }
                if (title != -1 && rating != -1)
                    return Tuple.Create(title, rating);
            }
            return null;
        }

        private static IList<HtmlNode> ExtractHeadCells(HtmlNode node)
        {
            if (node.Name != "table")
                return null;
            var row = node.ChildNodes.FirstOrDefault(n => n.Name == "tr");
            if (row == null)
                return null;
            var headNodes = row.ChildNodes.Where(IsHeadCell).ToList();
            if (headNodes.Count == 0)
                return null;
            return headNodes;
        }

        private static bool IsHeadCell(HtmlNode node)
        {
            if (node.Name != "td")
                return false;
            var headerAttrib = node.Attributes["class"];
            if (headerAttrib == null)
                return false;
            return headerAttrib.Value == "table_header";
        }

        private static Tuple<HtmlNode, HtmlNode> ExtractPayload(HtmlNode tableNode, int titleIndex, int ratingIndex)
        {
            var row = tableNode.Element("tr");
            if (row == null)
                return null;
            var cells = row.Elements("td").ToList();
            if (cells.Count < 2)
                return null;
            var linkNode = cells[titleIndex].ChildNodes.FirstOrDefault(n => n.Name == "a");
            if (linkNode == null)
                return null;
            var linkNodeClass = linkNode.Attributes["class"];
            if (linkNodeClass == null || linkNodeClass.Value != "animetitle")
                return null;
            return Tuple.Create(linkNode, cells[ratingIndex]);
        }

        private static AnimeRating ParseRatings(HtmlNode animeLink, HtmlNode ratingCell)
        {
            int id = Int32.Parse(captureRating.Match(animeLink.Attributes["href"].Value).Groups[1].Value);
            byte rating;
            if (ratingCell.InnerText != null && Byte.TryParse(ratingCell.InnerText, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, NumberFormatInfo.InvariantInfo, out rating))
            {
                return new AnimeRating(id, rating);
            }
            return new AnimeRating(id, 0);
        }
    }
}
