﻿
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Diagnostics;
using Newtonsoft.Json;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using Microsoft.ProjectOxford.Emotion;
using Microsoft.ProjectOxford.Emotion.Contract;
using Microsoft.ProjectOxford.Face;
using Microsoft.ProjectOxford.Face.Contract;
using Microsoft.ProjectOxford.Vision;
using VideoFrameAnalyzer;
using System.Configuration;
using System.Speech.Synthesis;
using System.IO;
using System.Windows.Threading;

namespace SmarterHome
{
    public class FamilyUser
    {
        
        public string name { get; set; }
        public Guid faceid;        
        public FamilyUser(string name, Guid faceid)
        {
            this.name = name;
            this.faceid = faceid;            

    }

    };
    public class FaceExtended : Face
    {
        public string name { get; set; }
    }


    public partial class MainWindow : System.Windows.Window
    {
        public string unknown_name = "Unknown";
        private FaceServiceClient _faceClient = null;
        private DateTime _questionTime;
        private bool personalQuestionHasBeenAsked = false;
        private bool personalQuestionHasBeenAnswered = false;
        TimeSpan ResponseTimeLimit = new TimeSpan(0, 1, 0);

        private readonly FrameGrabber<LiveCameraResult> _grabber = null;
        private static readonly ImageEncodingParam[] s_jpegParams = {
            new ImageEncodingParam(ImwriteFlags.JpegQuality, 60)
        };
        private readonly CascadeClassifier _localFaceDetector = new CascadeClassifier();
        private bool _fuseClientRemoteResults = true;
        private LiveCameraResult _latestResultsToDisplay = null;
        private AppMode _mode;
        private DateTime _startTime;
        bool AutoStopEnabled = true;
        TimeSpan AutoStopTime = new TimeSpan(0, 5, 0);
        TimeSpan AnalysisInterval = new TimeSpan(0, 0, 3);
        private Face[] current_faces;
        private UserDataContext dataContext;
        private User inhouseUser;
        private List<User> users;
        private List<FamilyUser> family_users = new List<FamilyUser>();
        private SpeechSynthesizer synth;

        public string[] names { get; set; }        

        public enum AppMode
        {
            Faces,
            Emotions,
            EmotionsWithClientFaceDetect
        }


        public MainWindow()
        {
            InitializeComponent();

            txtFamilyId.Text = "handoko";

            // Create grabber. 
            _grabber = new FrameGrabber<LiveCameraResult>();

            // Set up a listener for when the client receives a new frame.
            _grabber.NewFrameProvided += (s, e) =>
            {

                if (_fuseClientRemoteResults)
                {
                    // Local face detection. 
                    var rects = _localFaceDetector.DetectMultiScale(e.Frame.Image);
                    // Attach faces to frame. 
                    e.Frame.UserData = rects;
                }

                // The callback may occur on a different thread, so we must use the
                // MainWindow.Dispatcher when manipulating the UI. 
                this.Dispatcher.BeginInvoke((Action)(() =>
                {
                    // Display the image in the left pane.
                    //LeftImage.Source = e.Frame.Image.ToBitmapSource();

                    // If we're fusing client-side face detection with remote analysis, show the
                    // new frame now with the most recent analysis available. 
                    if (_fuseClientRemoteResults)
                    {
                        DisplayImageVisualization(e.Frame);
                    }
                }));

                // See if auto-stop should be triggered. 

                if (AutoStopEnabled && (DateTime.Now - _startTime) > AutoStopTime)
                {
                    _grabber.StopProcessingAsync();
                }
            };

            // Set up a listener for when the client receives a new result from an API call. 
            _grabber.NewResultAvailable += (s, e) =>
            {
                this.Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (e.TimedOut)
                    {
                        MessageArea.Text = "API call timed out.";
                    }
                    else if (e.Exception != null)
                    {
                        string apiName = "";
                        string message = e.Exception.Message;
                        var faceEx = e.Exception as FaceAPIException;
                        var emotionEx = e.Exception as Microsoft.ProjectOxford.Common.ClientException;
                        if (faceEx != null)
                        {
                            apiName = "Face";
                            message = faceEx.ErrorMessage;
                        }
                        else if (emotionEx != null)
                        {
                            apiName = "Emotion";
                            message = emotionEx.Error.Message;
                        }
                        MessageArea.Text = string.Format("{0} API call failed on frame {1}. Exception: {2}", apiName, e.Frame.Metadata.Index, message);
                    }
                    else
                    {
                        _latestResultsToDisplay = e.Analysis;

                        // Display the image and visualization in the right pane. 
                        if (!_fuseClientRemoteResults)
                        {
                            DisplayImageVisualization(e.Frame);

                        }
                    }
                }));
            };
            _localFaceDetector.Load(@"F:\Github\SmarterHome\SmarterHome\Data\haarcascade_frontalface_alt2.xml");
        }

        /// <summary> Function which submits a frame to the Face API. </summary>
        /// <param name="frame"> The video frame to submit. </param>
        /// <returns> A <see cref="Task{LiveCameraResult}"/> representing the asynchronous API call,
        ///     and containing the faces returned by the API. </returns>
        private async Task<LiveCameraResult> FacesAnalysisFunction(VideoFrame frame)
        {
            // Encode image. 
            var jpg = frame.Image.ToMemoryStream(".jpg", s_jpegParams);
            // Submit image to API. 
            var attrs = new List<FaceAttributeType> { FaceAttributeType.Age,
                FaceAttributeType.Gender, FaceAttributeType.HeadPose, FaceAttributeType.Smile };
            Face[] faces = await _faceClient.DetectAsync(jpg, returnFaceAttributes: attrs);

            current_faces = faces;

            names = new string[faces.Length];
            int index = 0;

            Guid[] familyFaces = new Guid[family_users.Count];
            //To avoid wasting api calls for already checked family members
            foreach (FamilyUser usr in family_users)
            {                
                familyFaces[index] = usr.faceid;
                index++;
            }



            //Verify againts the family member
            for(int i =0; i<faces.Length;i++)
            {
                names[i] = unknown_name;

                SimilarFace[] similarFaces = await _faceClient.FindSimilarAsync(faces[i].FaceId, familyFaces);

                foreach (SimilarFace sf in similarFaces)
                {
                    if(sf.Confidence > 0.5)
                    {
                        foreach(FamilyUser fu in family_users)
                        {
                            if(fu.faceid == sf.FaceId)
                            {
                                names[i] = fu.name;                                                                                          
                                break;
                            }
                        }                        
                    }
                }
            }


            // Count the API call. 
            //Properties.Settings.Default.FaceAPICallCount++;
            // Output. 
            return new LiveCameraResult { Faces = faces, Names = names};
        }



        private BitmapSource VisualizeResult(VideoFrame frame)
        {
            // Draw any results on top of the image. 
            BitmapSource visImage = frame.Image.ToBitmapSource();

            var result = _latestResultsToDisplay;

            if (result != null)
            {
                // See if we have local face detections for this image.
                var clientFaces = (OpenCvSharp.Rect[])frame.UserData;
                if (clientFaces != null && result.Faces != null)
                {
                    // If so, then the analysis results might be from an older frame. We need to match
                    // the client-side face detections (computed on this frame) with the analysis
                    // results (computed on the older frame) that we want to display. 
                    MatchAndReplaceFaceRectangles(result.Faces, clientFaces);
                }

                visImage = Visualization.DrawFaces(visImage, result.Faces, result.EmotionScores, result.CelebrityNames, names);
                visImage = Visualization.DrawTags(visImage, result.Tags);
            }

            return visImage;
        }

        private void MatchAndReplaceFaceRectangles(Face[] faces, OpenCvSharp.Rect[] clientRects)
        {
            // Use a simple heuristic for matching the client-side faces to the faces in the
            // results. Just sort both lists left-to-right, and assume a 1:1 correspondence. 

            // Sort the faces left-to-right. 
            var sortedResultFaces = faces
                .OrderBy(f => f.FaceRectangle.Left + 0.5 * f.FaceRectangle.Width)
                .ToArray();

            // Sort the clientRects left-to-right.
            var sortedClientRects = clientRects
                .OrderBy(r => r.Left + 0.5 * r.Width)
                .ToArray();

            // Assume that the sorted lists now corrrespond directly. We can simply update the
            // FaceRectangles in sortedResultFaces, because they refer to the same underlying
            // objects as the input "faces" array. 
            for (int i = 0; i < Math.Min(faces.Length, clientRects.Length); i++)
            {
                // convert from OpenCvSharp rectangles
                OpenCvSharp.Rect r = sortedClientRects[i];
                sortedResultFaces[i].FaceRectangle = new FaceRectangle { Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height };
            }
        }

        private async void btnStart_Click(object sender, RoutedEventArgs e)
        {
            //Check whether family exists
            dataContext = new UserDataContext();
            users = dataContext.Users.Where(x => x.familyid == txtFamilyId.Text.Trim()).ToList();
            if (users.Count == 0)
            {
                MessageBox.Show("Family not found!", "Error", MessageBoxButton.OK);
                return;
            }

            // Create API clients. 
            _faceClient = new FaceServiceClient(ConfigurationSettings.AppSettings.Get("faceapikey"));
//new FaceServiceClient()


            //Upload all the family member photos and get their face id
            try
            {
                foreach (User usr in users)
                {
                    using (var fileStream = File.OpenRead(usr.photofile))
                    {
                        var faces = await _faceClient.DetectAsync(fileStream, true, false);
                        if (faces.Count() > 0)
                        {
                            FamilyUser temp = new FamilyUser(usr.name, faces[0].FaceId);
                            family_users.Add(temp);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error: " + ex.Message, "Error", MessageBoxButton.OK);
                return;
            }

            ////label.Content = "Please wait...";
            if (users != null)
            {
            }
            //Check whether camera exists
            int numCameras = _grabber.GetNumCameras();

            if (numCameras == 0)
            {
                MessageArea.Text = "No cameras found!";
                return;
            }

            _mode = AppMode.Faces;
            _grabber.AnalysisFunction = FacesAnalysisFunction;                     

            // How often to analyze. 
            _grabber.TriggerAnalysisOnInterval(AnalysisInterval);

            // Reset message. 
            //MessageArea.Text = "";

            // Record start time, for auto-stop
            _startTime = DateTime.Now;


            await _grabber.StartProcessingCameraAsync(0);


        }

        public void DisplayImageVisualization(VideoFrame frame)
        {
            RightImage.Source = VisualizeResult(frame);
            if (names != null && current_faces != null && current_faces.Length == 1 && names.Length == 1)
                if(current_faces.Length == 1) 
                    sayHello();
        }

        public async void sayHello()
        {


            if (inhouseUser != null && personalQuestionHasBeenAsked && !personalQuestionHasBeenAnswered && (DateTime.Now - _questionTime) <= ResponseTimeLimit)
            {
                string personal_message = "Nice smile! sure thing {0} {1}. ";
                if(inhouseUser.defaulthometemprature >= 15)
                {
                    personal_message += "Please notice that this action will cost you an additional 50 pence per hour.";
                } 
                if (current_faces[0].FaceAttributes.Smile > 0.5 && !String.Equals(names[0], unknown_name))
                {
                    if (String.Equals(current_faces[0].FaceAttributes.Gender, "male"))
                    {
                        talk(String.Format(personal_message, "Mr.", names[0]));
                    }
                    else
                    {
                        talk(String.Format(personal_message, "Mrs.", names[0]));
                    }
                    personalQuestionHasBeenAnswered = true;
                }
                
            }

            if (!personalQuestionHasBeenAsked && current_faces.Length == 1 && !String.Equals(names[0], unknown_name))// && current_faces[0].FaceAttributes.Smile > 0.5)
            {
                string personal_message = "Your current house temperature is at 15 degrees, and we notice that you like your home temperature at {0} degrees. Would you like to {1} the heating?\nPlease smile to say yes.";
                string tempMsg = "";

                inhouseUser = dataContext.Users.SingleOrDefault(x => x.name == names[0] && x.familyid == txtFamilyId.Text);
                personal_message = String.Format(personal_message, inhouseUser.defaulthometemprature,
                    (inhouseUser.defaulthometemprature >= 15) ? "Turn on" : "Turn off"
                );
                if (String.Equals(current_faces[0].FaceAttributes.Gender, "male"))
                {
                    tempMsg = "Hello Mr. " + names[0] + ".\n" + personal_message;
                }
                else
                {
                    tempMsg = "Hello Mrs. " + names[0] + ".\n" + personal_message;
                }
                talk(tempMsg);                

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Start();
                timer.Tick += (sender, args) =>
                {
                    timer.Stop();                    
                };

                //MessageArea.Text += String.Format("\n{0} is Smiling", names[0]);
                personalQuestionHasBeenAsked = true;
                // Record start time, for auto-stop
                _questionTime = DateTime.Now;
            }

        }


        private Face CreateFace(FaceRectangle rect)
        {
            return new Face
            {
                FaceRectangle = new FaceRectangle
                {
                    Left = rect.Left,
                    Top = rect.Top,
                    Width = rect.Width,
                    Height = rect.Height
                }
            };
        }

        private Face CreateFace(Microsoft.ProjectOxford.Vision.Contract.FaceRectangle rect)
        {
            return new Face
            {
                FaceRectangle = new FaceRectangle
                {
                    Left = rect.Left,
                    Top = rect.Top,
                    Width = rect.Width,
                    Height = rect.Height
                }
            };
        }

        private Face CreateFace(Microsoft.ProjectOxford.Common.Rectangle rect)
        {
            return new Face
            {
                FaceRectangle = new FaceRectangle
                {
                    Left = rect.Left,
                    Top = rect.Top,
                    Width = rect.Width,
                    Height = rect.Height
                }
            };
        }

        private void txtFamilyId_GotFocus(object sender, RoutedEventArgs e)
        {
            if (txtFamilyId.Text.ToLower() == "familyid")
            {
                txtFamilyId.Text = "";
            }
        }

        private void button_Click(object sender, RoutedEventArgs e)
        {
            
        }

        private async void talk(string words)
        {
            if(synth != null)
                synth.Dispose();

            synth = new SpeechSynthesizer();

            synth.SetOutputToDefaultAudioDevice();

                // Speak a string synchronously.
            synth.SpeakAsync(words);
            

            MessageArea.Text = words;
        }

    }
}
