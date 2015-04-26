using System;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Drawing;
using System.Runtime.InteropServices;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Features2D;
using Emgu.CV.Structure;
using Emgu.CV.UI;
using Emgu.CV.Util;
using Emgu.CV.GPU;
using System.Diagnostics;
using System.IO;
using System.Xml;


namespace Parking_System
{
    public partial class Form1 : Form
    {
        private Capture _capture;
        private bool _captureInProgress;
        public Form1()
        {
            InitializeComponent();
        }
        
        private void ProcessFrame(object sender, EventArgs arg)
        {
                Image<Bgr, Byte> frame = _capture.QueryFrame().Resize(320, 240, Emgu.CV.CvEnum.INTER.CV_INTER_CUBIC);
                Image<Gray, Byte> grayframe = frame.Convert<Gray, Byte>();
                Image<Gray, Byte> modelImage = new Image<Gray, byte>("DataPlate/" + 10 + ".jpg");
                Image<Gray, Byte> observedImage = grayframe;
                Stopwatch watch;
                HomographyMatrix homography = null;
                SURFDetector surfCPU = new SURFDetector(500, false);
                VectorOfKeyPoint modelKeyPoints;
                VectorOfKeyPoint observedKeyPoints;
                Matrix<int> indices;
                Matrix<float> dist;
                Matrix<byte> mask;


                if (GpuInvoke.HasCuda)
                {
                    GpuSURFDetector surfGPU = new GpuSURFDetector(surfCPU.SURFParams, 0.01f);
                    using (GpuImage<Gray, Byte> gpuModelImage = new GpuImage<Gray, byte>(modelImage))

                    #region SURF
                    //extract features from the object image
                    using (GpuMat<float> gpuModelKeyPoints = surfGPU.DetectKeyPointsRaw(gpuModelImage, null))
                    using (GpuMat<float> gpuModelDescriptors = surfGPU.ComputeDescriptorsRaw(gpuModelImage, null, gpuModelKeyPoints))
                    using (GpuBruteForceMatcher matcher = new GpuBruteForceMatcher(GpuBruteForceMatcher.DistanceType.L2))
                    {
                        modelKeyPoints = new VectorOfKeyPoint();
                        surfGPU.DownloadKeypoints(gpuModelKeyPoints, modelKeyPoints);
                        watch = Stopwatch.StartNew();

                        // extract features from the observed image
                        using (GpuImage<Gray, Byte> gpuObservedImage = new GpuImage<Gray, byte>(observedImage))
                        using (GpuMat<float> gpuObservedKeyPoints = surfGPU.DetectKeyPointsRaw(gpuObservedImage, null))
                        using (GpuMat<float> gpuObservedDescriptors = surfGPU.ComputeDescriptorsRaw(gpuObservedImage, null, gpuObservedKeyPoints))
                        using (GpuMat<int> gpuMatchIndices = new GpuMat<int>(gpuObservedDescriptors.Size.Height, 2, 1))
                        using (GpuMat<float> gpuMatchDist = new GpuMat<float>(gpuMatchIndices.Size, 1))
                        {
                            observedKeyPoints = new VectorOfKeyPoint();
                            surfGPU.DownloadKeypoints(gpuObservedKeyPoints, observedKeyPoints);
                            matcher.KnnMatch(gpuObservedDescriptors, gpuModelDescriptors, gpuMatchIndices, gpuMatchDist, 2, null);
                            indices = new Matrix<int>(gpuMatchIndices.Size);
                            dist = new Matrix<float>(indices.Size);
                            gpuMatchIndices.Download(indices);
                            gpuMatchDist.Download(dist);

                            mask = new Matrix<byte>(dist.Rows, 1);

                            mask.SetValue(255);

                            Features2DTracker.VoteForUniqueness(dist, 0.8, mask);

                            int nonZeroCount = CvInvoke.cvCountNonZero(mask);
                            if (nonZeroCount >= 4)
                            {
                                nonZeroCount = Features2DTracker.VoteForSizeAndOrientation(modelKeyPoints, observedKeyPoints, indices, mask, 1.5, 20);
                                if (nonZeroCount >= 4)
                                    homography = Features2DTracker.GetHomographyMatrixFromMatchedFeatures(modelKeyPoints, observedKeyPoints, indices, mask, 3);
                            }

                            watch.Stop();
                        }
                    }
                    #endregion
                }
                else
                {
                    //extract features from the object image
                    modelKeyPoints = surfCPU.DetectKeyPointsRaw(modelImage, null);
                    //MKeyPoint[] kpts = modelKeyPoints.ToArray();
                    Matrix<float> modelDescriptors = surfCPU.ComputeDescriptorsRaw(modelImage, null, modelKeyPoints);
                    watch = Stopwatch.StartNew();

                    // extract features from the observed image
                    observedKeyPoints = surfCPU.DetectKeyPointsRaw(observedImage, null);
                    Matrix<float> observedDescriptors = surfCPU.ComputeDescriptorsRaw(observedImage, null, observedKeyPoints);
                    BruteForceMatcher matcher = new BruteForceMatcher(BruteForceMatcher.DistanceType.L2F32);
                    matcher.Add(modelDescriptors);
                    int k = 2;
                    indices = new Matrix<int>(observedDescriptors.Rows, k);
               
                    dist = new Matrix<float>(observedDescriptors.Rows, k);
                    matcher.KnnMatch(observedDescriptors, indices, dist, k, null);

                    mask = new Matrix<byte>(dist.Rows, 1);

                    mask.SetValue(255);

                    Features2DTracker.VoteForUniqueness(dist, 0.8, mask);

                    int nonZeroCount = CvInvoke.cvCountNonZero(mask);
                    if (nonZeroCount >= 20)
                    {
                        nonZeroCount = Features2DTracker.VoteForSizeAndOrientation(modelKeyPoints, observedKeyPoints, indices, mask, 1.5, 20);
                        if (nonZeroCount >= 20)
                        {
                            homography = Features2DTracker.GetHomographyMatrixFromMatchedFeatures(modelKeyPoints, observedKeyPoints, indices, mask, 3);
                            XMLData();
                        }
                        else
                        {
                            textBox1.Text = string.Empty;
                            textBox2.Text = string.Empty;
                            textBox3.Text = string.Empty;
                            textBox4.Text = string.Empty;
                            textBox5.Text = string.Empty;
                        }
                    }
                    watch.Stop();
                #region draw the projected region on the image
                if (homography != null)
                {  //draw a rectangle along the projected model
                    Rectangle rect = modelImage.ROI;
                    PointF[] pts = new PointF[] { 
                new PointF(rect.Left, rect.Bottom),
                new PointF(rect.Right, rect.Bottom),
                new PointF(rect.Right, rect.Top),
                new PointF(rect.Left, rect.Top)};
                    homography.ProjectPoints(pts);
                    frame.DrawPolyline(Array.ConvertAll<PointF, Point>(pts, Point.Round), true, new Bgr(Color.Red), 2);
                }
                #endregion
                CaptureImageBox.Image = frame;
                DataImageBox.Image = modelImage;
            }
        }

        private void CaptureButton_Click(object sender, EventArgs e)
        {
            #region if capture is not created, create it now
            if (_capture == null)
            {
                try
                {
                    _capture = new Capture();
                }
                catch (NullReferenceException excpt)
                {
                    MessageBox.Show(excpt.Message);
                }
            }
            #endregion

            if (_capture != null)
            {
                if (_captureInProgress)
                {  //stop the capture
                    CaptureButton.Text = "Start Capture";
                    Application.Idle -= ProcessFrame;
                }
                else
                {
                    //start the capture
                    CaptureButton.Text = "Stop";
                    Application.Idle += ProcessFrame;
                }

                _captureInProgress = !_captureInProgress;
            }
        }

        private void ReleaseData()
      {
         if (_capture != null)
            _capture.Dispose();
      }
        private void Form1_Load(object sender, EventArgs e)
        {

        }
        public void XMLData()
        {
            XmlDocument Data = new XmlDocument();
            Data.Load(@"C:\Users\PostMan\Documents\Visual Studio 2010\Projects\Parking_System\Parking_System\DataPlate.xml");

            XmlNodeList elements = Data.SelectNodes("//Data/Car");
            
            foreach (XmlElement element in elements)
            {

                textBox1.Text = element["Plate"].InnerText;
                textBox2.Text = element["Province"].InnerText;
                textBox3.Text = element["Name"].InnerText;
                textBox4.Text = element["Surname"].InnerText;
                textBox5.Text = element["Rank"].InnerText;
            }
        }
    }
}
