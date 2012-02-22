namespace kwm
{
    partial class RunningOverlayControl
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

        #region Component Designer generated code

        /// <summary> 
        /// Required method for Designer support - do not modify 
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.lblSubject = new System.Windows.Forms.Label();
            this.lblText = new System.Windows.Forms.Label();
            this.btnStop = new System.Windows.Forms.Button();
            this.picIcon = new System.Windows.Forms.PictureBox();
            this.iconTimer = new System.Windows.Forms.Timer(this.components);
            this.durationTimer = new System.Windows.Forms.Timer(this.components);
            ((System.ComponentModel.ISupportInitialize)(this.picIcon)).BeginInit();
            this.SuspendLayout();
            // 
            // lblSubject
            // 
            this.lblSubject.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
                        | System.Windows.Forms.AnchorStyles.Right)));
            this.lblSubject.AutoEllipsis = true;
            this.lblSubject.Font = new System.Drawing.Font("Microsoft Sans Serif", 9F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblSubject.Location = new System.Drawing.Point(40, 7);
            this.lblSubject.Name = "lblSubject";
            this.lblSubject.Size = new System.Drawing.Size(187, 18);
            this.lblSubject.TabIndex = 7;
            this.lblSubject.Text = "My Desktop (Support mode)";
            // 
            // lblText
            // 
            this.lblText.Font = new System.Drawing.Font("Microsoft Sans Serif", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblText.Location = new System.Drawing.Point(40, 25);
            this.lblText.Name = "lblText";
            this.lblText.Size = new System.Drawing.Size(168, 18);
            this.lblText.TabIndex = 6;
            this.lblText.Text = "( 11:25 min)";
            // 
            // btnStop
            // 
            this.btnStop.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnStop.BackColor = System.Drawing.SystemColors.ControlLight;
            this.btnStop.Location = new System.Drawing.Point(233, 13);
            this.btnStop.Name = "btnStop";
            this.btnStop.Size = new System.Drawing.Size(48, 26);
            this.btnStop.TabIndex = 4;
            this.btnStop.Text = "Stop";
            this.btnStop.UseVisualStyleBackColor = false;
            this.btnStop.Click += new System.EventHandler(this.btnStop_Click);
            // 
            // picIcon
            // 
            this.picIcon.Image = WmRes.empty;
            this.picIcon.InitialImage = null;
            this.picIcon.Location = new System.Drawing.Point(5, 14);
            this.picIcon.Name = "picIcon";
            this.picIcon.Size = new System.Drawing.Size(24, 24);
            this.picIcon.SizeMode = System.Windows.Forms.PictureBoxSizeMode.AutoSize;
            this.picIcon.TabIndex = 5;
            this.picIcon.TabStop = false;
            // 
            // iconTimer
            // 
            this.iconTimer.Interval = 800;
            this.iconTimer.Tick += new System.EventHandler(this.iconTimer_Tick);
            // 
            // durationTimer
            // 
            this.durationTimer.Interval = 1000;
            this.durationTimer.Tick += new System.EventHandler(this.durationTimer_Tick);
            // 
            // RunningOverlayControl
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.lblSubject);
            this.Controls.Add(this.lblText);
            this.Controls.Add(this.picIcon);
            this.Controls.Add(this.btnStop);
            this.Name = "RunningOverlayControl";
            this.Size = new System.Drawing.Size(289, 51);
            ((System.ComponentModel.ISupportInitialize)(this.picIcon)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label lblSubject;
        private System.Windows.Forms.Label lblText;
        private System.Windows.Forms.PictureBox picIcon;
        private System.Windows.Forms.Button btnStop;
        private System.Windows.Forms.Timer iconTimer;
        private System.Windows.Forms.Timer durationTimer;
    }
}
