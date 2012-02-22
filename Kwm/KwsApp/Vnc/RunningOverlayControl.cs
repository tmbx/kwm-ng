using kcslib;
using kwmlib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;

namespace kwm
{
    public partial class RunningOverlayControl : UserControl
    {
        /// <summary>
        /// Reference to the local session.
        /// </summary>
        private VncLocalSession m_session;

        /// <summary>
        /// Is the next icon to display the full one?
        /// (used to create a blinking effect)
        /// </summary>
        private bool m_fullIcon = true;

        public RunningOverlayControl()
        {
            InitializeComponent();
        }

        public void Relink(VncLocalSession session)
        {
            m_session = session;

            durationTimer.Enabled = true;
            iconTimer.Enabled = true;

            UpdateLabels();
            UpdateDuration();
        }

        public void Terminate()
        {
            durationTimer.Enabled = false;
            iconTimer.Enabled = false;
        }

        private void UpdateLabels()
        {
            lblSubject.Text = m_session.Subject;
        }
        /// <summary>
        /// Update the Duration field. Must be called by a timer at every 1 sec.
        /// </summary>
        private void UpdateDuration()
        {
            TimeSpan duration = DateTime.Now - m_session.CreationTime;

            String strDuration;
            if (duration.Days == 0)
            {
                strDuration = String.Format("{0:d2}:{1:d2}:{2:d2}", duration.Hours, duration.Minutes, duration.Seconds);
            }
            else if (duration.Days == 1)
            {
                strDuration = String.Format("{0} day, {1:d2}:{2:d2}:{3:d2}", duration.Days, duration.Hours, duration.Minutes, duration.Seconds);
            }
            else
            {
                strDuration = String.Format("{0} days, {1:d2}:{2:d2}:{3:d2}", duration.Days, duration.Hours, duration.Minutes, duration.Seconds);
            }

            lblText.Text = "(" + strDuration + ")";
        }

        private void iconTimer_Tick(object sender, EventArgs e)
        {
            if (m_fullIcon) picIcon.Image = WmRes.full;
            else picIcon.Image = WmRes.empty;
            m_fullIcon = !m_fullIcon;
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            m_session.Cancel();
        }

        private void durationTimer_Tick(object sender, EventArgs e)
        {
            UpdateDuration();
        }
    }
}
