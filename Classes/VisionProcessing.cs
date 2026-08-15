using System;
using System.Data;
using System.Drawing;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace PickandPlace2026.Classes
{
    public class VisionProcessing
    {
        private Bitmap? currentImage;

        /// <summary>
        /// Holds the error message from the most recent operation, if any.
        /// Callers (UI layer) can check this after a method returns false/empty
        /// and display it however is appropriate (e.g. a ContentDialog).
        /// </summary>
        public string LastError { get; private set; } = string.Empty;

        public VisionProcessing(Bitmap currentImage)
        {
            this.currentImage = currentImage;
        }

        public VisionProcessing()
        {
            this.currentImage = null;
        }

        public DataTable MakeDataTable(DataTable dt)
        {
            dt.Columns.Add("Loc1X", typeof(double));
            dt.Columns.Add("Loc1Y", typeof(double));
            dt.Columns.Add("Loc2X", typeof(double));
            dt.Columns.Add("Loc2Y", typeof(double));
            dt.Columns.Add("Loc3X", typeof(double));
            dt.Columns.Add("Loc3Y", typeof(double));
            dt.Columns.Add("Loc4X", typeof(double));
            dt.Columns.Add("Loc4Y", typeof(double));
            dt.Columns.Add("ItemWidth", typeof(double));
            dt.Columns.Add("ItemHeight", typeof(double));
            dt.Columns.Add("LocAngle", typeof(double));
            dt.Columns.Add("OffsetX", typeof(double));
            dt.Columns.Add("OffsetY", typeof(double));
            dt.Columns.Add("Text", typeof(string));

            return dt;
        }

        public bool ApplySobelEdgeFilter()
        {
            LastError = string.Empty;

            if (currentImage != null)
            {
                try
                {
                    using (Mat src = currentImage.ToMat())
                    using (Mat dst = new Mat())
                    {
                        Cv2.Sobel(src, dst, MatType.CV_8U, 1, 1);
                        ReplaceCurrentImage(dst);
                    }
                    return true;
                }
                catch (Exception e)
                {
                    LastError = e.ToString();
                }
            }
            return false;
        }

        public bool ApplyCannyEdgeDetector()
        {
            LastError = string.Empty;

            if (currentImage != null)
            {
                try
                {
                    using (Mat src = currentImage.ToMat())
                    using (Mat dst = new Mat())
                    {
                        Cv2.Canny(src, dst, 50, 150);
                        ReplaceCurrentImage(dst);
                    }
                    return true;
                }
                catch (Exception e)
                {
                    LastError = e.ToString();
                }
            }
            return false;
        }

        public bool ApplyConvertToGrayscale()
        {
            LastError = string.Empty;

            if (currentImage != null)
            {
                try
                {
                    using (Mat src = currentImage.ToMat())
                    using (Mat dst = new Mat())
                    {
                        Cv2.CvtColor(src, dst, ColorConversionCodes.BGR2GRAY);
                        ReplaceCurrentImage(dst);
                    }
                    return true;
                }
                catch (Exception e)
                {
                    LastError = "applyConvertToGrayscale: " + e.ToString();
                }
            }
            return false;
        }

        /// <summary>
        /// Finds rectangular blobs in the current image (expected to already be an
        /// edge map - callers typically run ApplyConvertToGrayscale then
        /// ApplyCannyEdgeDetector first) and reports the largest one's position,
        /// size and rotation, plus its offset in mm from the image center.
        /// </summary>
        public VisionLocationResult LocateObjects()
        {
            LastError = string.Empty;

            VisionLocationResult ld = new VisionLocationResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "", 0, 0, null);

            if (currentImage == null) return ld;

            try
            {
                using (Mat edgeMat = currentImage.ToMat())
                using (Mat displayMat = new Mat())
                {
                    if (edgeMat.Channels() == 1)
                        Cv2.CvtColor(edgeMat, displayMat, ColorConversionCodes.GRAY2BGR);
                    else
                        edgeMat.CopyTo(displayMat);

                    int totalWidth = edgeMat.Width;
                    int totalHeight = edgeMat.Height;

                    Cv2.FindContours(edgeMat, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);

                    // pick the largest contour that meets the old AForge blob
                    // counter's minimum 10x10px size, same as before
                    double bestArea = 0;
                    RotatedRect? bestRect = null;

                    foreach (OpenCvSharp.Point[] contour in contours)
                    {
                        Rect bounds = Cv2.BoundingRect(contour);
                        if (bounds.Width < 10 || bounds.Height < 10) continue;

                        double area = Cv2.ContourArea(contour);
                        if (area > bestArea)
                        {
                            bestArea = area;
                            bestRect = Cv2.MinAreaRect(contour);
                        }
                    }

                    if (bestRect.HasValue)
                    {
                        RotatedRect rect = bestRect.Value;
                        Point2f[] corners = rect.Points();

                        ld.Loc1X = corners[0].X; ld.Loc1Y = corners[0].Y;
                        ld.Loc2X = corners[1].X; ld.Loc2Y = corners[1].Y;
                        ld.Loc3X = corners[2].X; ld.Loc3Y = corners[2].Y;
                        ld.Loc4X = corners[3].X; ld.Loc4Y = corners[3].Y;

                        if (rect.Size.Width >= rect.Size.Height)
                        {
                            ld.ItemWidth = rect.Size.Width;
                            ld.ItemHeight = rect.Size.Height;
                            ld.LocAngle = rect.Angle;
                        }
                        else
                        {
                            ld.ItemWidth = rect.Size.Height;
                            ld.ItemHeight = rect.Size.Width;
                            ld.LocAngle = rect.Angle + 90;
                        }

                        ld.OffsetY = PixelsToMM((rect.Center.X - (totalWidth / 2.0)) * -1);
                        ld.OffsetX = PixelsToMM(rect.Center.Y - (totalHeight / 2.0));

                        ld.LocText = $"width: {ld.ItemWidth:F1}{Environment.NewLine}height: {ld.ItemHeight:F1}{Environment.NewLine}"
                                    + $"Center X: {rect.Center.X:F1} Center Y: {rect.Center.Y:F1}{Environment.NewLine}"
                                    + $"Image Center: {totalWidth / 2} x {totalHeight / 2}";

                        OpenCvSharp.Point[] corners32 = Array.ConvertAll(corners, c => (OpenCvSharp.Point)c);
                        Cv2.Polylines(displayMat, new[] { corners32 }, true, Scalar.Blue, 2);
                    }

                    // draw center cross lines on image
                    Cv2.Line(displayMat, new OpenCvSharp.Point(0, totalHeight / 2), new OpenCvSharp.Point(totalWidth, totalHeight / 2), Scalar.White, 1);
                    Cv2.Line(displayMat, new OpenCvSharp.Point(totalWidth / 2, 0), new OpenCvSharp.Point(totalWidth / 2, totalHeight), Scalar.White, 1);

                    ld.Image = displayMat.ToBitmap();
                    ReplaceCurrentImage(displayMat);
                }

                return ld;
            }
            catch (Exception e)
            {
                LastError = e.ToString();
            }
            return ld;
        }

        private double PixelsToMM(double pixels)
        {
            return pixels / 5;
        }

        private void ReplaceCurrentImage(Mat mat)
        {
            Bitmap? old = currentImage;
            currentImage = mat.ToBitmap();
            old?.Dispose();
        }

        public void SetCurrentImage(Bitmap currentImage)
        {
            this.currentImage = currentImage;
        }

        public Bitmap? GetCurrentImage()
        {
            return currentImage;
        }
    }
}
