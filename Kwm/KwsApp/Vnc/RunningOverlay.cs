using kcslib;
using kwmlib;
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Drawing;

namespace kwm
{
    public partial class RunningOverlay : Form
    {
        private bool m_isDragging = false;
        private Point m_mouseLocation = new Point(0, 0);

        public RunningOverlay()
        {
            InitializeComponent();
        }

        public void Relink(VncLocalSession session)
        {
            Text = "Screen Sharing in '" + session.Kws.GetKwsName() + "'";
            overlayCtrl.Relink(session);
            SetLocation();
            Show();
        }

        public void Terminate()
        {
            overlayCtrl.Terminate();
            Close();
        }

        /// <summary>
        /// Set the overlay's location according to the saved information.
        /// </summary>
        private void SetLocation()
        {
            Rectangle r = SystemInformation.VirtualScreen;
            Point loc = LoadLocation();

            // If the saved location is too much to the left or to the right,
            // move it back to the left side.
            if (loc.X < r.X || loc.X > r.Width) loc.X = r.X;

            // Conversely, if the saved location is too much to top 
            // or to the bottom, move it back to the top.
            if (loc.Y < r.Y || loc.Y > r.Height) loc.Y = r.Y;

            SaveLocation(loc);
            Location = loc;
        }

        private void RunningOverlay_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                m_isDragging = true;
                m_mouseLocation = e.Location;
            }
        }

        private void RunningOverlay_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                m_isDragging = false;
        }

        private void RunningOverlay_MouseMove(object sender, MouseEventArgs e)
        {
            if (m_isDragging)
            {
                Point t = new Point();
                t.X = Location.X + (e.X - m_mouseLocation.X);
                t.Y = Location.Y + (e.Y - m_mouseLocation.Y);
                this.Location = t;
            }
        }

        private void RunningOverlay_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveLocation(Location);
        }

        private void RunningOverlay_LocationChanged(object sender, EventArgs e)
        {
            if (this.Location.Y < 10)
                this.Location = new Point(this.Location.X, 0);
        }

        private Point LoadLocation()
        {
            return Wm.Cd.VncOverlayLoc;
        }

        private void SaveLocation(Point loc)
        {
            if (loc.Equals(Wm.Cd.VncOverlayLoc)) return;
            Wm.Cd.VncOverlayLoc = loc;
            Wm.OnStateChange(WmStateChange.Internal);
        }
    }
}