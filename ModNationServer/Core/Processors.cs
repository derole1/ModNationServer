﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Web;
using System.Xml;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Data.SQLite;
using HttpMultipartParser;

namespace ModNationServer
{
    static class Processors
    {
        //List of xml schema files so were not constantly reading from disk for each request
        public static Dictionary<string,  byte[]> xmlSchemas = new Dictionary<string, byte[]>();

        public static void MainServerProcessor(HttpListenerContext context)
        {
            try
            {
                //Sets up various things for http request and response
                HttpListenerRequest request = context.Request;
                HttpListenerResponse response = context.Response;
                System.IO.Stream input = request.InputStream;
                System.IO.Stream output = response.OutputStream;
                try
                {
                    //Reads the http data
                    byte[] recvBuffer = new byte[request.ContentLength64];
                    if (request.ContentLength64 > 0)
                    {
                        int recvBytes = 0;
                        MemoryStream ms = new MemoryStream();
                        do
                        {
                            byte[] tempBuf = new byte[20000];
                            int bytes = input.Read(tempBuf, 0, tempBuf.Length);
                            ms.Write(tempBuf, 0, bytes);
                            recvBytes += bytes;
                        } while (recvBytes < request.ContentLength64);
                        recvBuffer = ms.ToArray();
                    }
                    byte[] buffer = recvBuffer;
                    //Creates receiving and response xml documents
                    XmlDocument recDoc = new XmlDocument();
                    XmlDocument resDoc = new XmlDocument();
                    //Sets up response with proper values
                    XmlElement result = AppendCommon(resDoc);
                    resDoc.AppendChild(result);
                    //if (request.HttpMethod == "POST")
                    //{
                    //    recDoc.LoadXml(Encoding.UTF8.GetString(recvBuffer));
                    //}
                    Console.WriteLine("Request URL: {0}", request.RawUrl.Split('?')[0]);
                    Dictionary<string, string> urlEncodedData = new Dictionary<string, string>();
                    //Decode url encoding if urlencoded data is sent
                    if (request.ContentType == "application/x-www-form-urlencoded")
                    {
                        DecodeURLEncoding(Encoding.UTF8.GetString(recvBuffer), urlEncodedData);
                    }
                    //Open a database connection (TODO: Put into DatabaseManager?)
                    SQLiteConnection sqlite_conn = new SQLiteConnection("Data Source=database.sqlite;Version=3;");
                    sqlite_conn.Open();
                    SQLiteCommand sqlite_cmd = sqlite_conn.CreateCommand();
                    bool respond = false;
                    bool isXml = true;
                    string[] url = request.RawUrl.Substring(1, request.RawUrl.Length - 1).Split('/');
                    //Test page
                    if (!request.UserAgent.Contains("PlayerConnect"))
                    {
                        response.ContentType = "text/html";
                        buffer = Encoding.ASCII.GetBytes("<h1>Your PS3 is connecting to ModNation!</h1>");
                        response.ContentLength64 = buffer.Length;
                        output.Write(buffer, 0, buffer.Length);
                        output.Close();
                        return;
                    }
                    //Decide if the server wants resources, player creations or wants to access database stuff
                    switch (url[0])
                    {
                        case "resources":
                            respond = true;
                            isXml = false;
                            if (File.Exists(string.Join("\\", url)))
                            {
                                response.ContentType = "application/xml; charset=utf-8";
                                buffer = xmlSchemas[string.Join("\\", url)];
                            }
                            break;
                        case "player_avatars":
                            respond = true;
                            isXml = false;
                            response.ContentType = "image/png";
                            if (File.Exists(string.Join("\\", url)))
                            {
                                buffer = File.ReadAllBytes(string.Join("\\", url));
                            }
                            break;
                        case "player_creations":
                            respond = true;
                            isXml = false;
                            if (File.Exists(string.Join("\\", url)))
                            {
                                //Decide what the content is and set the mime type accordingly
                                if (Path.GetExtension(string.Join("\\", url)) == ".png") { response.ContentType = "image/png"; }
                                else { response.ContentType = "application/octet-stream"; }
                                buffer = File.ReadAllBytes(string.Join("\\", url));
                            }
                            break;
                        default:
                            //This probably isnt needed, but was just put in for debugging purposes
                            //response.AddHeader("X-Rack-Cache", "pass");
                            //response.AddHeader("X-Runtime", "7");
                            //response.AddHeader("Last-Modified", "Thu, 31 Dec 2037 23:55:55 GMT");
                            //response.AddHeader("Expires", "Thu, 31 Dec 2037 23:55:55 GMT");
                            //response.AddHeader("Cache-Control", "private, max-age=0, must-revalidate");
                            //response.SetCookie(new Cookie("playerconnect_session_id", request.Cookies["playerconnect_session_id"].Value));
                            break;
                    }
                    if (isXml)
                    {
                        //response.ContentType = "text/xml; charset=utf-8";
                        //Game requires the "charset=utf-8" part to properly decode some xml responses
                        response.ContentType = "application/xml; charset=utf-8";
                        int paramStart = request.RawUrl.IndexOf('?') + 1;
                        //BIG switch statement that decides what handler the data goes to
                        switch (url[0].Split('?')[0])
                        {
                            case "preferences.xml":
                                respond = Handlers.PreferencesUpdateHandler(request, response, urlEncodedData, resDoc);
                                break;
                            case "policy.view.xml":
                                string sessionID = SessionManager.RandomSessionID(0x20);
                                response.SetCookie(new Cookie("playerconnect_session_id", SessionManager.EncodeInitialSessionID(sessionID)));
                                DecodeURLEncoding(request.RawUrl.Substring(paramStart, request.RawUrl.Length - paramStart), urlEncodedData);
                                respond = Handlers.PolicyViewHandler(request, response, urlEncodedData, resDoc);
                                break;
                            case "policy.accept.xml":
                                respond = Handlers.PolicyAcceptHandler(request, response, urlEncodedData, resDoc);
                                break;
                            case "session.login_np.xml":
                                respond = Handlers.SessionLoginHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                break;
                            case "session.ping.xml":
                                respond = Handlers.SessionPingHandler(request, response, urlEncodedData, resDoc);
                                break;
                            case "profanity_filter.list.xml":
                                respond = Handlers.ProfanityFilterListHandler(request, response, urlEncodedData, resDoc);
                                break;
                            default:
                                //Check if session exists for requests that require auth
                                if (SessionManager.PingSession(request.Cookies["playerconnect_session_id"].Value))
                                {
                                    Console.WriteLine("SESSION ID: {0}", SessionManager.GetSessionID(request.Cookies["playerconnect_session_id"].Value));
                                    response.SetCookie(new Cookie("playerconnect_session_id", request.Cookies["playerconnect_session_id"].Value));
                                    response.SetCookie(new Cookie("path", "/"));
                                    switch (url[0].Split('?')[0])
                                    {
                                        case "session.set_presence.xml":
                                            respond = Handlers.SessionSetPresenceHandler(request, response, urlEncodedData, resDoc);
                                            break;
                                        case "content_url.list.xml":
                                            respond = Handlers.ContentUrlListHandler(request, response, urlEncodedData, resDoc);
                                            break;
                                        case "skill_level.list.xml":
                                            respond = Handlers.SkillLevelListHandler(request, response, urlEncodedData, resDoc);
                                            break;
                                        case "player_creation.mine.xml":
                                            DecodeURLEncoding(request.RawUrl.Substring(paramStart, request.RawUrl.Length - paramStart), urlEncodedData);
                                            respond = Handlers.PlayerCreationMineHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "player_creation.show.xml":
                                            DecodeURLEncoding(request.RawUrl.Substring(paramStart, request.RawUrl.Length - paramStart), urlEncodedData);
                                            respond = Handlers.PlayerCreationShowHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "player_creation.download.xml":
                                            respond = Handlers.PlayerCreationDownloadHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "player_creation.list.xml":
                                            DecodeURLEncoding(request.RawUrl.Substring(paramStart, request.RawUrl.Length - paramStart), urlEncodedData);
                                            respond = Handlers.PlayerCreationListHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "player_creation.search.xml":
                                            DecodeURLEncoding(request.RawUrl.Substring(paramStart, request.RawUrl.Length - paramStart), urlEncodedData);
                                            respond = Handlers.PlayerCreationSearchHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "player_creation.verify.xml":
                                            respond = Handlers.PlayerCreationVerifyHandler(request, response, urlEncodedData, resDoc);
                                            break;
                                        case "player_creation.create.xml":
                                            respond = Handlers.PlayerCreationCreateHandler(request, response, MultipartFormDataParser.Parse(new MemoryStream(recvBuffer)), resDoc, sqlite_cmd);
                                            break;
                                        case "player_creation.destroy.xml":
                                            respond = Handlers.PlayerCreationDestroyHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "player_creation.friends_view.xml":
                                            DecodeURLEncoding(request.RawUrl.Substring(paramStart, request.RawUrl.Length - paramStart), urlEncodedData);
                                            respond = Handlers.PlayerCreationFriendsViewHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "player_creation_complaint.create.xml":
                                            respond = Handlers.PlayerCreationComplaintCreateHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "player_creation_rating.view.xml":
                                            DecodeURLEncoding(request.RawUrl.Substring(paramStart, request.RawUrl.Length - paramStart), urlEncodedData);
                                            respond = Handlers.PlayerCreationRatingViewHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "player_creation_rating.create.xml":
                                            respond = Handlers.PlayerCreationRatingCreateHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "player_creation_rating.list.xml":
                                            DecodeURLEncoding(request.RawUrl.Substring(paramStart, request.RawUrl.Length - paramStart), urlEncodedData);
                                            respond = Handlers.PlayerCreationRatingListHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "player.to_id.xml":
                                            DecodeURLEncoding(request.RawUrl.Substring(paramStart, request.RawUrl.Length - paramStart), urlEncodedData);
                                            respond = Handlers.PlayerToIdHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "mail_message.create.xml":
                                            respond = Handlers.MailMessageCreateHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "tag.list.xml":
                                            respond = Handlers.TagListHandler(request, response, urlEncodedData, resDoc);
                                            break;
                                        case "player_metric.show.xml":
                                            respond = Handlers.PlayerMetricShowHandler(request, response, urlEncodedData, resDoc);
                                            break;
                                        case "player_metric.update.xml":
                                            respond = Handlers.PlayerMetricUpdateHandler(request, response, urlEncodedData, resDoc);
                                            break;
                                        case "mail_message.list.xml":
                                            DecodeURLEncoding(request.RawUrl.Substring(paramStart, request.RawUrl.Length - paramStart), urlEncodedData);
                                            respond = Handlers.MailMessageListHandler(request, response, urlEncodedData, resDoc);
                                            break;
                                        case "achievement.list.xml":
                                            DecodeURLEncoding(request.RawUrl.Substring(paramStart, request.RawUrl.Length - paramStart), urlEncodedData);
                                            respond = Handlers.AchievementListHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "content_update.latest.xml":
                                            DecodeURLEncoding(request.RawUrl.Substring(paramStart, request.RawUrl.Length - paramStart), urlEncodedData);
                                            respond = Handlers.ContentUpdateLatestHandler(request, response, urlEncodedData, resDoc);
                                            break;
                                        case "leaderboard.view.xml":
                                            DecodeURLEncoding(request.RawUrl.Substring(paramStart, request.RawUrl.Length - paramStart), urlEncodedData);
                                            respond = Handlers.LeaderboardViewHandler(request, response, urlEncodedData, resDoc);
                                            break;
                                        case "leaderboard.player_stats.xml":
                                            DecodeURLEncoding(request.RawUrl.Substring(paramStart, request.RawUrl.Length - paramStart), urlEncodedData);
                                            respond = Handlers.LeaderboardPlayerStatsHandler(request, response, urlEncodedData, resDoc);
                                            break;
                                        case "announcement.list.xml":
                                            respond = Handlers.AnnouncementListHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "player.info.xml":
                                            DecodeURLEncoding(request.RawUrl.Substring(paramStart, request.RawUrl.Length - paramStart), urlEncodedData);
                                            respond = Handlers.PlayerInfoHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "player_rating.create.xml":
                                            respond = Handlers.PlayerRatingCreateHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "player_complaint.create.xml":
                                            respond = Handlers.PlayerComplaintCreateHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "favorite_player.create.xml":
                                            respond = Handlers.FavoritePlayerCreateHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "favorite_player.list.xml":
                                            DecodeURLEncoding(request.RawUrl.Substring(paramStart, request.RawUrl.Length - paramStart), urlEncodedData);
                                            respond = Handlers.FavoritePlayerListHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "favorite_player.remove.xml":
                                            respond = Handlers.FavoritePlayerRemoveHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "single_player_game.create_finish_and_post_stats.xml":
                                            //respond = Handlers.SinglePlayerGameCreateFinishAndPostStatsHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "player_profile.update.xml":
                                            respond = Handlers.PlayerProfileUpdateHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        case "player_avatar.update.xml":
                                            respond = Handlers.PlayerAvatarUpdateHandler(request, response, MultipartFormDataParser.Parse(new MemoryStream(recvBuffer)), resDoc, sqlite_cmd);
                                            break;
                                        case "server.select.xml":
                                            respond = Handlers.ServerSelectHandler(request, response, urlEncodedData, resDoc, sqlite_cmd);
                                            break;
                                        default:
                                            respond = Handlers.DefaultHandler(request, response, urlEncodedData, resDoc);
                                            Console.WriteLine("Unimplemented request!");
                                            break;
                                    }
                                }
                                else
                                {
                                    //respond = true;
                                    //resDoc.ChildNodes[0].ChildNodes[0].ChildNodes[0].InnerText = "-105";
                                    //resDoc.ChildNodes[0].ChildNodes[0].ChildNodes[1].InnerText = "NP Auth Failed: ticket is expired";
                                    sqlite_conn.Close();
                                    output.Close();
                                    response.Close();
                                }
                                break;
                        }
                    }
                    sqlite_conn.Close();
                    //Determine if we need to respond and if the data is xml
                    if (respond)
                    {
                        if (isXml)
                        {
                            //Console.WriteLine("Response XML: {0}", resDoc.InnerXml);
                            buffer = Encoding.UTF8.GetBytes(resDoc.InnerXml);
                        }
                        //Calculate an S3 ETag (Required for image previews), its just an MD5 hash
                        response.AddHeader("ETag", "\"" + BitConverter.ToString(MD5.Create().ComputeHash(buffer)).Replace("-", "").ToLower() + "\"");
                        response.KeepAlive = false;
                        response.ContentLength64 = buffer.Length;
                        output.Write(buffer, 0, buffer.Length);
                    }
                }
                catch (Exception e) { try { File.AppendAllText("error.log", e.ToString() + "\n\n"); } catch { } }
                output.Close();
            } catch { }
        }

        public static void DirectoryServerProcessor(string service, TcpClient client, X509Certificate2 cert)
        {
            try
            {
                //Here we process the client and send the data to the proper handler
                NetworkStream stream = client.GetStream();
                SslStream ssl = new SslStream(stream, false);
                ssl.AuthenticateAsServer(cert, false, System.Security.Authentication.SslProtocols.Default, false);
                Console.WriteLine("New client authenticated");
                byte[] responseBuffer = new byte[255];
                while (client.Connected)
                {
                    XmlDocument recDoc = GetXmlDoc(ReadData(ssl));
                    Thread.Sleep(30000);
                    //Drop the connection for now as we dont know response format
                    //ssl.Close();
                    //client.Close();
                    //return;
                    XmlDocument resDoc = InitResXml(recDoc);
                    switch (recDoc.GetElementsByTagName("method")[0].InnerText.Split(' ')[1])
                    {
                        case "startConnect":
                            DirectoryHandlers.StartConnectHandler(service, ssl, recDoc, resDoc);
                            break;
                        case "timeSyncRequest":
                            DirectoryHandlers.TimeSyncRequestHandler(service, ssl, recDoc, resDoc);
                            break;
                        case "getServiceList":
                            DirectoryHandlers.GetServiceListHandler(service, ssl, recDoc, resDoc);
                            break;
                        default:
                            DirectoryHandlers.DefaultHandler(service, ssl, recDoc, resDoc);
                            break;
                    }
                    Console.WriteLine("Lobbying server response: {0}", resDoc.InnerXml);
                }
                ssl.Close();
            } catch { }
            Console.WriteLine("Client disconnected");
            client.Close();
        }

        static int cnt = 0;

        static byte[] ReadData(SslStream ssl)
        {
            //Need to be able to read stream multiple times to get all data
            byte[] buffer = new byte[0];
            int bytesExpected = 0;
            int bytesRead = 0;
            int count = 0;
            do
            {
                byte[] tempBuf = new byte[1024];
                int bytes = ssl.Read(tempBuf, 0, tempBuf.Length);
                buffer = buffer.Concat(tempBuf.Take(bytes).ToArray()).ToArray();
                bytesRead += bytes;
                if (bytesExpected == 0)
                {
                    bytesExpected = BitConverter.ToInt32(tempBuf.Take(4).Reverse().ToArray(), 0);
                    Console.WriteLine("Expected {0}", bytesExpected);
                }
                //if (bytes < 1024) { break; }
                count++;
            } while (bytesRead < bytesExpected);
            File.WriteAllBytes(cnt.ToString() + ".bin", buffer);
            count++;
            return buffer;
        }

        static XmlDocument GetXmlDoc(byte[] data)
        {
            //Decodes xml data from lobby server
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(Encoding.UTF8.GetString(data, 24, data.Length - 24));
            Console.WriteLine("Lobbying server request: {0}", doc.InnerXml);
            return doc;
        }

        static XmlDocument InitResXml(XmlDocument recDoc)
        {
            //Creates response body
            XmlDocument doc = new XmlDocument();
            XmlElement service = doc.CreateElement("service");
            service.SetAttribute("name", recDoc.ChildNodes[0].Attributes["name"].InnerText);
            XmlElement transaction = doc.CreateElement("transaction");
            transaction.SetAttribute("id", recDoc.ChildNodes[0].ChildNodes[0].Attributes["id"].InnerText);
            transaction.SetAttribute("type", "TRANSACTION_TYPE_REPLY");
            service.AppendChild(transaction);
            doc.AppendChild(service);
            return doc;
        }

        public static XmlElement AppendCommon(XmlDocument resDoc)
        {
            //Appends common response data to the response xml document
            XmlElement result = resDoc.CreateElement("result");
            XmlElement status = resDoc.CreateElement("status");
            XmlElement id = resDoc.CreateElement("id");
            XmlElement message = resDoc.CreateElement("message");
            id.InnerText = "0";
            message.InnerText = "Successful completion";
            status.AppendChild(id);
            status.AppendChild(message);
            result.AppendChild(status);
            return result;
        }

        public static void DecodeURLEncoding(string url, Dictionary<string, string> dict)
        {
            //Function to add url encoded data to a dictionary
            url = HttpUtility.UrlDecode(url);
            //Console.WriteLine("URL Encoded data! Printing...");
            foreach (string entry in url.Split('&'))
            {
                string[] urlSplit = entry.Split('=');
                Console.WriteLine("Key={0}, Value={1}", urlSplit[0], urlSplit[1]);
                dict.Add(urlSplit[0], urlSplit[1]);
            }
            //Console.WriteLine("End of URL Encoded data");
        }

        //Was going to decode multi part data but I gave up and just used a library instead
        //public static void DecodeBoundaryEncoding(string data, string contentType, Dictionary<string, string> dict)
        //{
        //    Console.WriteLine("Boundary Encoded data! Printing...");
        //    string boundary = contentType.Split("; ".ToCharArray())[1];
        //    Console.WriteLine("Boundary: {0}", boundary);
        //    foreach (string entry in data.Split(boundary.ToCharArray()))
        //    {
        //        string[] lineSplit = entry.Split('\n');
        //        foreach (string line in lineSplit)
        //        {
        //            Console.WriteLine("Printing boundary data...");
        //            if (line[0] == 'C')
        //            {
        //                Dictionary<string, string> headerList = new Dictionary<string, string>();
        //                foreach (string header in line.Split(';'))
        //                {
        //                    string[] headerSplit = header.Split('=');
        //                    headerList.Add(headerSplit[0].Replace(" ", ""), headerSplit[1]);
        //                    Console.WriteLine("Key={0}, Value={1}", headerSplit[0].Replace(" ", ""), headerSplit[1]);
        //                }
        //            }
        //        }
        //        Console.WriteLine("Key={0}, Value={1}", entry.Substring(entry.IndexOf("name=\""), entry.IndexOf("\";") - entry.IndexOf("name=\"")), entry.Substring(entry.Length));
        //        dict.Add(urlSplit[0], urlSplit[1]);
        //    }
        //    Console.WriteLine("End of Boundary Encoded data");
        //}
    }
}
