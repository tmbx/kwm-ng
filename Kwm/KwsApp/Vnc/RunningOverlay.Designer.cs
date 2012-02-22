namespace kwm
{
    partial class RunningOverlay
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(RunningOverlay));
            this.overlayCtrl = new RunningOverlayControl();
            this.SuspendLayout();
            // 
            // overlayCtrl
            // 
            this.overlayCtrl.Dock = System.Windows.Forms.DockStyle.Fill;
            this.overlayCtrl.Location = new System.Drawing.Point(0, 0);
            this.overlayCtrl.Name = "overlayCtrl";
            this.overlayCtrl.Size = new System.Drawing.Size(305, 71);
            this.overlayCtrl.TabIndex = 0;
            // 
            // RunningOverlay
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.SystemColors.ControlLight;
            this.ClientSize = new System.Drawing.Size(305, 71);
            this.ControlBox = false;
            this.Controls.Add(this.overlayCtrl);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedToolWindow;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "RunningOverlay";
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            this.Text = "Live Screen Sharing Session";
            this.TopMost = true;
            this.MouseUp += new System.Windows.Forms.MouseEventHandler(this.RunningOverlay_MouseUp);
            this.MouseDown += new System.Windows.Forms.MouseEventHandler(this.RunningOverlay_MouseDown);
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.RunningOverlay_FormClosing);
            this.MouseMove += new System.Windows.Forms.MouseEventHandler(this.RunningOverlay_MouseMove);
            this.LocationChanged += new System.EventHandler(this.RunningOverlay_LocationChanged);
            this.ResumeLayout(false);

        }

        #endregion

        private RunningOverlayControl overlayCtrl;
    }
}