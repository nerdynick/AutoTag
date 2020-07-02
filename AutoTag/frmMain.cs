﻿using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using Microsoft.VisualBasic;
using static Microsoft.VisualBasic.Interaction; // needed to use MsgBox, which is nicer than MessageBox.show as you can define the style using a single paramter, no idea why this isn't just in C# already

using TvDbSharper;
using TMDbLib.Client;

namespace AutoTag {
    public partial class frmMain : Form {
        public frmMain(string[] args) {
            InitializeComponent();
            if (args.Length > 1) { // if arguments provided
                AddToTable(args.Skip(1).ToArray()); // add all except first argument to table (first argument is executing file name)
                btnProcess_Click(this, new EventArgs());
            }
            cBoxMode.SelectedIndex = Properties.Settings.Default.defaultTaggingMode; // set mode
        }

        private bool taskRunning = false; // flag for if process started
        private CancellationTokenSource cts = new CancellationTokenSource();
        private bool errorsEncountered = false; // flag for if process encountered errors or not
        private int mode;

        private async Task ProcessFilesAsync(CancellationToken ct) {
            IProcessor processor;
            if (mode == 0) {
                ITvDbClient tvdb = new TvDbClient();
                await tvdb.Authentication.AuthenticateAsync("TQLC3N5YDI1AQVJF");
                processor = new TVProcessor(tvdb);
            } else {
                TMDbClient tmdb = new TMDbClient("b342b6005f86daf016533bf0b72535bc");
                processor = new MovieProcessor(tmdb);
            }

            foreach (DataGridViewRow row in tblFiles.Rows) {
                if (ct.IsCancellationRequested) { // exit loop if cancellation requested
                    break;
                }
                bool fileSuccess = true;

                SetRowStatus(row, "Unprocessed"); // reset row status and colour
                SetRowColour(row, "#FFFFFF");

                IncrementPBarValue();

                tblFiles.Invoke(new MethodInvoker(() => tblFiles.CurrentCell = row.Cells[0]));

                FileMetadata metadata = await processor.process(new TableUtils(tblFiles), row, this);

                if (metadata.Success == false) {
                    errorsEncountered = true;
                    continue;
                } else if (metadata.Complete == false) {
                    errorsEncountered = true;
                    fileSuccess = false;
                }

                #region Tag Writing
                if (Properties.Settings.Default.tagFiles == true) {
                    try {
                        TagLib.File file = TagLib.File.Create(row.Cells[0].Value.ToString());

                        file.Tag.Title = metadata.Title;
                        file.Tag.Comment = metadata.Overview;
                        file.Tag.Genres = new string[] { (metadata.FileType == FileMetadata.Types.TV) ? "TVShows" : "Movie" };

                        if (metadata.FileType == FileMetadata.Types.TV) {
                            file.Tag.Album = metadata.SeriesName;
                            file.Tag.Disc = (uint) metadata.Season;
                            file.Tag.Track = (uint) metadata.Episode;
                        } else {
                            file.Tag.Year = (uint) metadata.Date.Year;
                        }

                        if (metadata.CoverFilename != "" && Properties.Settings.Default.addCoverArt == true) { // if there is an image available and cover art is enabled
                            string downloadPath = Path.Combine(Path.GetTempPath(), "autotag");
                            string downloadFile = Path.Combine(downloadPath, metadata.CoverFilename);

                            if (!File.Exists(downloadFile)) { // only download file if it hasn't already been downloaded
                                if (!Directory.Exists(downloadPath)) {
                                    Directory.CreateDirectory(downloadPath); // create temp directory
                                }

                                try {
                                    using (WebClient client = new WebClient()) {
                                        client.DownloadFile(metadata.CoverURL, downloadFile); // download image
                                    }
                                    file.Tag.Pictures = new TagLib.Picture[] { new TagLib.Picture(downloadFile) { Filename = "cover.jpg" } };

                                } catch (WebException ex) {
                                    SetRowError(row, "Error: Failed to download cover art - " + ex.Message);
                                    fileSuccess = false;
                                }
                            } else {
                                file.Tag.Pictures = new TagLib.Picture[] { new TagLib.Picture(downloadFile) { Filename = "cover.jpg" } }; // overwrite default file name - allows software such as Icaros to display cover art thumbnails - default isn't compliant with Matroska guidelines
                            }
                        } else if (String.IsNullOrEmpty(metadata.CoverFilename)) {
                            fileSuccess = false;
                        }

                        file.Save();

                        if (fileSuccess == true) {
                            SetRowStatus(row, "Successfully tagged file as " + metadata);
                        }

                    } catch (Exception ex) {
                        SetRowError(row, "Error: Could not tag file - " + ex.Message);
                        fileSuccess = false;
                    }
                }
                #endregion

                #region Renaming
                if (Properties.Settings.Default.renameFiles == true) {
                    string newPath;
                    if (mode == 0) {
                        newPath = Path.Combine(
                            Path.GetDirectoryName(row.Cells[0].Value.ToString()),
                            EscapeFilename(String.Format(GetTVRenamePattern(), metadata.SeriesName, metadata.Season, metadata.Episode.ToString("00"), metadata.Title) + Path.GetExtension(row.Cells[0].Value.ToString()))
                            );
                    } else {
                        newPath = Path.Combine(
                            Path.GetDirectoryName(row.Cells[0].Value.ToString()),
                            EscapeFilename(String.Format(GetMovieRenamePattern(), metadata.Title, metadata.Date.Year)) + Path.GetExtension(row.Cells[0].Value.ToString()));
                    }

                    if (row.Cells[0].Value.ToString() != newPath) {
                        try {
                            File.Move(row.Cells[0].Value.ToString(), newPath);
                            SetCellValue(row.Cells[0], newPath);
                        } catch (Exception ex) {
                            SetRowError(row, "Error: Could not rename file - " + ex.Message);
                            fileSuccess = false;
                        }
                    }
                }

                if (fileSuccess == true) {
                    SetRowColour(row, "#4CAF50");
                    if (mode == 0) {
                        SetRowStatus(row, "Success - tagged as " + String.Format(GetTVRenamePattern(), metadata.SeriesName, metadata.Season, metadata.Episode.ToString("00"), metadata.Title));
                    } else {
                        SetRowStatus(row, "Success - tagged as " + String.Format(GetMovieRenamePattern(), metadata.Title, metadata.Date.Year));
                    }
                }
                #endregion

            }

            if (errorsEncountered == false) {
                Invoke(new MethodInvoker(() => MsgBox("Files successfully processed.", MsgBoxStyle.Information, "Process Complete")));
            } else {
                Invoke(new MethodInvoker(() => MsgBox("Errors were encountered during processing. See the highlighted files for details.", MsgBoxStyle.Critical, "Process Complete")));
            }

            Invoke(new MethodInvoker(() => SetButtonState(true)));
            taskRunning = false; // reset flag
        }

        #region Button click handlers
        private void btnAddFile_Click(object sender, EventArgs e) {
            if (dlgAddFile.ShowDialog() == DialogResult.OK) {
                AddToTable(dlgAddFile.FileNames);
            }
            pBarProcessed.Value = 0;
        }

        private void btnAddFolder_Click(object sender, EventArgs e) {
            if (dlgAddFolder.ShowDialog() == DialogResult.OK) {
                AddToTable(new string[] { dlgAddFolder.SelectedPath });
            }
            pBarProcessed.Value = 0;
        }

        private void btnRemove_Click(object sender, EventArgs e) {
            if (tblFiles.CurrentRow != null) {
                tblFiles.Rows.Remove(tblFiles.CurrentRow);
            }
            pBarProcessed.Value = 0;
        }

        private void btnClear_Click(object sender, EventArgs e) {
            tblFiles.Rows.Clear();
            pBarProcessed.Value = 0;
        }

        private void btnProcess_Click(object sender, EventArgs e) {
            if (tblFiles.RowCount > 0) {

                if (taskRunning == false) {
                    pBarProcessed.Maximum = tblFiles.RowCount;
                    SetButtonState(false); // disable all buttons
                    pBarProcessed.Value = 0;
                    errorsEncountered = false;
                    taskRunning = true;
                    mode = cBoxMode.SelectedIndex;

                    Task processFiles = Task.Run(() => ProcessFilesAsync(cts.Token), cts.Token); // run task with cancellation token attached
                } else {
                    cts.Cancel(); // request cancellation if requested
                    cts = new CancellationTokenSource(); // create new token source so process can be restarted
                }

            }
        }
        #endregion

        #region Add to table
        private void AddToTable(string[] files) {
            foreach (String file in files) {
                if (File.GetAttributes(file).HasFlag(FileAttributes.Directory)) { // if file is actually a directory, add the all the files in the directory
                    AddToTable(Directory.GetFileSystemEntries(file));
                } else {
                    AddSingleToTable(file);
                }
            }
        }

        private void AddSingleToTable(string file) {
            if (tblFiles.Rows.Cast<DataGridViewRow>().Where(row => row.Cells[0].Value.ToString() == file).Count() == 0 && new[] { ".mp4", ".m4v", ".mkv" }.Contains(Path.GetExtension(file))) { // check file is not already added and has correct extension
                tblFiles.Rows.Add(file, "Unprocessed");
            }
        }
        #endregion

        #region UI Invokers
        private void SetRowError(DataGridViewRow row, string errorMsg) {
            if (row.Cells[1].Value.ToString().Contains("Error")) { // if error already encountered
                SetCellValue(row.Cells[1], row.Cells[1].Value.ToString() + Environment.NewLine + errorMsg);
            } else {
                SetCellValue(row.Cells[1], errorMsg);
            }
            SetRowColour(row, "#E57373");
            errorsEncountered = true;
        }

        private void SetRowStatus(DataGridViewRow row, string msg) {
            SetCellValue(row.Cells[1], msg);
        }

        private void IncrementPBarValue() {
            Invoke(new MethodInvoker(() => pBarProcessed.Value += 1));
        }

        private void SetCellValue(DataGridViewCell cell, Object value) {
            tblFiles.Invoke(new MethodInvoker(() => cell.Value = value));
        }

        private void SetRowColour(DataGridViewRow row, string hex) {
            tblFiles.Invoke(new MethodInvoker(() => row.DefaultCellStyle.BackColor = ColorTranslator.FromHtml(hex)));
        }
        #endregion

        #region Utility functions
        private string EscapeFilename(string filename) {
            return string.Join("", filename.Split(Path.GetInvalidFileNameChars()));
        }

        private void SetButtonState(bool state) {
            btnAddFile.Enabled = state;
            btnAddFolder.Enabled = state;
            btnRemove.Enabled = state;
            lblMode.Enabled = state;
            cBoxMode.Enabled = state;
            btnClear.Enabled = state;
            btnProcess.Text = (state) ? "Process Files" : "Cancel"; // set button text
            MenuStrip.Enabled = state;
            AllowDrop = state;
        }

        private string GetTVRenamePattern() { // Get usable renaming pattern
            return Properties.Settings.Default.renamePatternTV.Replace("%1", "{0}").Replace("%2", "{1}").Replace("%3", "{2}").Replace("%4", "{3}");
        }

        private string GetMovieRenamePattern() { // Get usable renaming pattern
            return Properties.Settings.Default.renamePatternMovie.Replace("%1", "{0}").Replace("%2", "{1}");
        }
        #endregion

        #region ToolStrip
        private void exitToolStripMenuItem_Click(object sender, EventArgs e) {
            Environment.Exit(0);
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e) {
            Form form = new frmAbout();
            form.ShowDialog();
        }

        private void optionsToolStripMenuItem_Click(object sender, EventArgs e) {
            Form form = new frmOptions();
            form.ShowDialog();
        }
        #endregion

        #region Drag and drop
        private void frmMain_DragEnter(object sender, DragEventArgs e) {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) {
                e.Effect = DragDropEffects.Copy;
            }
        }

        private void frmMain_DragDrop(object sender, DragEventArgs e) {
            AddToTable((string[]) e.Data.GetData(DataFormats.FileDrop));
        }
        #endregion

        private void frmMain_FormClosing(object sender, FormClosingEventArgs e) {
            if (Directory.Exists(Path.GetTempPath() + "\\autotag\\")) {
                foreach (FileInfo file in new DirectoryInfo(Path.GetTempPath() + "\\autotag\\").GetFiles()) {
                    file.Delete(); // clean up temporary files
                }
            }
        }
    }
}
