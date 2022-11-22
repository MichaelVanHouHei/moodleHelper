using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Flurl.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace moodleHelper
{
    public class Quiz
    {
        public string title { get; set; }
        public int cmid { get; set; }
        public string sessKey { get; set; }
        public string attempt { get; set; }
        public int pageCount { get; set; }
        public CookieSession cookieSession { get; set; }
        public HeaderCollection headers { get; set; }
        public async Task<bool> SubmitAllPageAnswer()
        {
            bool isAllDone = (await Task.WhenAll(Enumerable.Range(0, pageCount).Select(_ => SubmitAnswer(_)))).All(x=>x);
            if(isAllDone)
            {
                var response = await cookieSession.Request($"https://ummoodle.um.edu.mo/mod/quiz/processattempt.php?cmid={cmid}")
             .AllowAnyHttpStatus()
             .WithAutoRedirect(false)
             .WithHeaders(headers.ToDictionary(t => t.Name, t => t.Value))
             .PostUrlEncodedAsync($"attempt={attempt}&finishattempt=1&timeup=0&slots=&cmid={cmid}&sesskey={sessKey}");
                if (response.StatusCode == 303)
                {
                    var location = response.Headers.FirstOrDefault(x => x.Item1 == "Location").Item2;
                    if(location.Contains("review"))
                    {
                        Console.WriteLine($"done quiz:{title}--{sessKey}---{attempt}---{pageCount}");
                        return true;
                    }
                    else
                    {
                        Console.WriteLine("get review failed");
                    }
                }
                else
                {
                    Console.WriteLine("error occur after submit all answers");
                }
            }
            else
            {
                Console.WriteLine("some question not done");
            }
            return false;
        }
        public async Task<bool> SubmitAnswer(int page)
        {
            var attemptResponse = await cookieSession.Request($"https://ummoodle.um.edu.mo/mod/quiz/attempt.php?attempt={attempt}&cmid={cmid}&page={page}").WithHeaders(headers.ToDictionary(t => t.Name, t => t.Value)).GetStringAsync();
            var parser = new HtmlParser();
            var document = await parser.ParseDocumentAsync(attemptResponse);
            var parameters = document.All.Where(x => x.LocalName == "input" && x.GetAttribute("name") != null && x.GetAttribute("type") != null && x.GetAttribute("type") =="hidden").Select(c=>new  { name = c.GetAttribute("name"),value = c.GetAttribute("value")}).ToList();
            //   var qid = Regex.Match(parameters.First(x => x.name.Contains("flagged")).name, "q([0-9]+):[0-9]+_:flagged", RegexOptions.Compiled).Groups[1].Value;
            var q = parameters.First(x => x.name.Contains("flagged")).name.Split('_')[0] + "_answer";
            //var a = 1;
            parameters.Add(new { name = q, value = "1" }); 
            var submitResponse = await cookieSession.Request($"https://ummoodle.um.edu.mo/mod/quiz/processattempt.php?cmid={cmid}")
                .AllowAnyHttpStatus()
                .WithAutoRedirect(false)
                .WithHeaders(headers.ToDictionary(t => t.Name, t => t.Value))
                .PostMultipartAsync(mp => { foreach (var p in parameters) mp.AddString(p.name, p.value); } );
            if (submitResponse.StatusCode != 303) return false;
            var location = submitResponse.Headers.FirstOrDefault(x => x.Item1 == "Location").Item2;
            return (location != null && location.Contains("/mod/quiz/attempt.php?"));
           
        }
    }
    internal class Program
    {
        static ProxyServer proxyServer = new ProxyServer();
        static CookieSession cookieSession = new CookieSession();
        private static async Task OnBeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
        {
            var isChangeState = e.HttpClient.Request.RequestUri.Host.Contains("ummoodle.um.edu.mo");
            if (!isChangeState) return;
            e.DecryptSsl =true;
        }
        static HeaderCollection headers = new HeaderCollection();
        public static async Task OnRequest(object sender, SessionEventArgs e)
        {
            // sub headers to flurl 
            if(e.HttpClient.Request.Url.Contains("https://ummoodle.um.edu.mo/course/view.php?id="))
            {
                headers = e.HttpClient.Request.Headers;
                Console.WriteLine($"proxy headers count :{headers.Count()}");

            }
        }
        static async Task BeginQuiz(Quiz quiz)
        {
            var viewResponse = await cookieSession.Request("https://ummoodle.um.edu.mo/mod/quiz/view.php?id=" + quiz.cmid).WithHeaders(headers.ToDictionary(t => t.Name, t => t.Value)).GetStringAsync();
            //   Console.WriteLine(viewResponse);
          //  System.IO.File.WriteAllText("testing.txt", viewResponse);
            var parser = new HtmlParser();
            var document = await parser.ParseDocumentAsync(viewResponse);
            var sessKey = document.All.First(x => x.LocalName == "input" && x.GetAttribute("name") != null && x.GetAttribute("name") == "sesskey").GetAttribute("value");
            quiz.sessKey = sessKey;
            Console.WriteLine($"Quiz:{quiz.cmid}--title:{quiz.title}--sesskeys:{quiz.sessKey}");
            var res1 =  await cookieSession.Request($"https://ummoodle.um.edu.mo/mod/quiz/startattempt.php").WithHeaders(headers.ToDictionary(t => t.Name, t => t.Value)).AllowAnyHttpStatus().WithAutoRedirect(false).PostUrlEncodedAsync($"cmid={quiz.cmid.ToString()}&sesskey={sessKey}");
            var location = res1.Headers.FirstOrDefault(x => x.Item1 == "Location").Item2;
            if (location != null && location.Contains("/mod/quiz/attempt.php?"))
            {
                string attempt = HttpUtility.ParseQueryString(new Uri(location).Query)
                    .Get("attempt");
                quiz.attempt = attempt;
                var attemptResponse = await cookieSession.Request(location).WithHeaders(headers.ToDictionary(t => t.Name, t => t.Value)).GetStringAsync();
                document = await parser.ParseDocumentAsync(attemptResponse);
                var pageCount = document.All.Count(x => x.LocalName == "a" && x.GetAttribute("id") != null && x.GetAttribute("id").Contains("quiznavbutton"));
                Console.WriteLine($"page count found:{pageCount} --- attempt:{attempt}");
                quiz.pageCount= pageCount;
                quiz.headers = headers;
                quiz.cookieSession = cookieSession;
                await quiz.SubmitAllPageAnswer();
            }
            else
            {
                Console.WriteLine($"error for quiz:{quiz.cmid}");
            }
        }
      
        static async Task<string> getGradeHtml(string tid)
        {
            var response =await cookieSession.Request("https://ummoodle.um.edu.mo/grade/report/user/index.php?id=" + tid).WithHeaders(headers.ToDictionary(t=>t.Name,t=>t.Value)).GetStringAsync();
            var parser = new HtmlParser();
            if (!response.Contains("Grade")) return "error";
            var document = await parser.ParseDocumentAsync(response);
            var gradeTable = document.All.First(x=>x.LocalName == "table" && x.GetAttribute("summary") != null && x.GetAttribute("summary").Contains("graded"));
            return gradeTable.ToHtml();
        }
        static async Task<IEnumerable<Quiz>> getAllQuiz(SessionEventArgs session)
        {
            Console.WriteLine("dumping all the quiz");
            string tid =  Regex.Match( session.HttpClient.Request.Url , "id=([0-9]+)").Groups[1].Value ;
            var parser = new  HtmlParser();
            var document =await parser.ParseDocumentAsync(await session.GetResponseBodyAsString());
            var courseHeader = document.QuerySelector(".page-header-headings > h1:nth-child(1)");
            Console.WriteLine($"course found {courseHeader.InnerHtml} ");
            var gradeHtml = await getGradeHtml(tid);
            document.QuerySelector("#page-header").InnerHtml = gradeHtml;
            courseHeader.TextContent = "Fuck you mother son of bitch"; 
            //var allQuizBlocks = document.All.Where(e => e.LocalName == "div" && e.GetAttribute("class") == "activityinstance").Select(y => y.GetElementsByClassName("aalink"));
            var allQuizBlocks = document.All.Where(e => e.LocalName == "a" && e.GetAttribute("href") != null && e.GetAttribute("href").Contains("/mod/quiz/view.php?id=")).Select(y => new Quiz
            {
                cmid = int.Parse(y.GetAttribute("href").Replace("https://ummoodle.um.edu.mo/mod/quiz/view.php?id=", "")),
                title =  y.GetElementsByClassName("instancename").First().TextContent,
            }) ;
            session.SetResponseBodyString(document.ToHtml());
            Console.WriteLine($"Quiz found:{allQuizBlocks.Count()}");
            return allQuizBlocks;
        }
        public static async Task OnResponse(object sender, SessionEventArgs e)
        {
            if (e.HttpClient.Request.Url.Contains("/course/view.php?id="))
            {
                // parse all quiz
                var quiz = await getAllQuiz(e);
             
                 await Task.WhenAll( quiz.Select(q=> BeginQuiz(q) ));
                
            }
        }
        static void Main(string[] args)
        {

            proxyServer.EnableHttp2 = true;
            proxyServer.CertificateManager.CreateRootCertificate(false);
            proxyServer.CertificateManager.TrustRootCertificate();
            proxyServer.BeforeRequest += OnRequest;
            proxyServer.BeforeResponse += OnResponse;
            var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 8000, true)
            {
                // Use self-issued generic certificate on all https requests
                // Optimizes performance by not creating a certificate for each https-enabled domain
                // Useful when certificate trust is not required by proxy clients
                //GenericCertificate = new X509Certificate2(Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "genericcert.pfx"), "password")
            };
            explicitEndPoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnectRequest;
            proxyServer.AddEndPoint(explicitEndPoint);
            proxyServer.Start();
            proxyServer.SetAsSystemHttpProxy(explicitEndPoint);
            proxyServer.SetAsSystemHttpsProxy(explicitEndPoint);
       
            while(true)
            {
                if (Console.ReadLine() == "exit")
                {
                    explicitEndPoint.BeforeTunnelConnectRequest -= OnBeforeTunnelConnectRequest;
                    proxyServer.BeforeRequest -= OnRequest;
                    proxyServer.BeforeResponse -= OnResponse;
                    proxyServer.Stop();
                }

            }
        }
    }
}
