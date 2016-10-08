﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Athame.CommonModel;
using Athame.DownloadAndTag;
using Athame.InternalModel;
using Athame.Properties;
using Microsoft.WindowsAPICodePack.Dialogs;
using Microsoft.WindowsAPICodePack.Taskbar;

namespace Athame.UI
{
    public partial class MainForm : Form
    {
        // Constants
        private const string GroupHeaderFormat = "{0}: {1} ({2})";

        // Read-only instance vars
        private readonly List<DownloadableMediaCollection> mDownloadItems = new List<DownloadableMediaCollection>();
        private readonly TaskbarManager mTaskbarManager = TaskbarManager.Instance;
        private readonly Dictionary<int, List<int>> mGroupAndQueueIndices = new Dictionary<int, List<int>>();
        private readonly string mPathFormat;

        // Instance vars
        private Tuple<int, int> mSelectedItem;
        private UrlParseResult mResult;
        private Service mService;
        // shitty hack
        private int currentCollection;

        public MainForm()
        {
            InitializeComponent();
            mPathFormat = Path.Combine(
                            ApplicationSettings.Default.SaveLocation,
                            ApplicationSettings.Default.TrackFilenameFormat);
            queueImageList.Images.Add("warning", Resources.warning);
            queueImageList.Images.Add("error", Resources.error);
        }

        #region Download queue manipulation
        private void AddToQueue(DownloadableMediaCollection item)
        {
            mDownloadItems.Add(item);
            var header = String.Format(GroupHeaderFormat, item.CollectionType, item.Name, item.Service.Name);
            var group = new ListViewGroup(header);
            var groupIndex = queueListView.Groups.Add(group);
            if (!mGroupAndQueueIndices.ContainsKey(groupIndex)) 
                mGroupAndQueueIndices[groupIndex] = new List<int>(item.Tracks.Count);
            foreach (var t in item.Tracks)
            {
                var lvItem = new ListViewItem { Group = group };
                if (t.CommonTrack.IsDownloadable)
                {
                    lvItem.Checked = true;
                }
                else
                {
                    lvItem.BackColor = SystemColors.Control;
                    lvItem.ForeColor = SystemColors.GrayText;
                    lvItem.ImageKey = "error";
                }
                if (File.Exists(t.Path))
                {
                    t.WillDownload = false;
                    lvItem.Checked = false;
                    lvItem.ImageKey = "warning";
                }
                lvItem.SubItems.Add(t.CommonTrack.TrackNumber + "/" + t.CommonTrack.DiscNumber);
                lvItem.SubItems.Add(t.CommonTrack.Title);
                lvItem.SubItems.Add(t.CommonTrack.Artist);
                lvItem.SubItems.Add(t.CommonTrack.Album.Title);
                group.Items.Add(lvItem);
                var newItem = queueListView.Items.Add(lvItem);
                mGroupAndQueueIndices[groupIndex].Add(newItem.Index);
            }
        }

        private void RemoveFromQueue(DownloadableMediaCollection item)
        {
            var itemIndex = mDownloadItems.IndexOf(item);
            mDownloadItems.RemoveAt(itemIndex);
            var containingGroup = queueListView.Groups[itemIndex];
            containingGroup.Items.Clear();
            queueListView.Groups.RemoveAt(itemIndex);
        }

        private Tuple<int, int> GetIndicesOfCollectionAndTrack(int listViewIndex)
        {
            foreach (var index in mGroupAndQueueIndices)
            {
                int itemIndex;
                if ((itemIndex = index.Value.IndexOf(listViewIndex)) > -1)
                {
                    mSelectedItem = new Tuple<int, int>(index.Key, itemIndex);
                    return mSelectedItem;
                }
            }
            mSelectedItem = null;
            return null;
        }

        private int GetListViewIndexOfCollectionAndTrack(Tuple<int, int> indices)
        {
            return mGroupAndQueueIndices[indices.Item1][indices.Item2];
        }
#endregion

        private void LockUi()
        {
            idTextBox.Enabled = false;
            dlButton.Enabled = false;
            settingsButton.Enabled = false;
            pasteButton.Enabled = false;
            clearButton.Enabled = false;
            startDownloadButton.Enabled = false;
        }

        private void UnlockUi()
        {
            idTextBox.Enabled = true;
            dlButton.Enabled = true;
            settingsButton.Enabled = true;
            pasteButton.Enabled = true;
            clearButton.Enabled = true;
            startDownloadButton.Enabled = true;
        }

        private void PrepareForNextTrack(Track track, int current, int count)
        {
            // Put leading zero in front of track number
            var fmt = String.Format("[{0}/{1}] {2:D2}: {3} - {4} - {5}",
                                  current + 1, count, track.TrackNumber, track.Title, track.Artist, track.Album.Title);
            currTrackLabel.Text = fmt;
        }

        private void SetGlobalProgress(int value)
        {
            totalProgressBar.Value = value;
            mTaskbarManager.SetProgressValue(value, totalProgressBar.Maximum);
        }

        private void SetGlobalProgressState(ProgressBarState state)
        {
            totalProgressBar.SetState(state);
            switch (state)
            {
                case ProgressBarState.Normal:
                    mTaskbarManager.SetProgressState(TaskbarProgressBarState.Normal);
                    break;
                case ProgressBarState.Error:
                    mTaskbarManager.SetProgressState(TaskbarProgressBarState.Normal);
                    break;
                case ProgressBarState.Warning:
                    mTaskbarManager.SetProgressState(TaskbarProgressBarState.Paused);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("state", state, null);
            }
        }

        private void PresentException(Exception ex)
        {
            SetGlobalProgressState(ProgressBarState.Error);
            var th = "An error occurred";
            if (ex is ResourceNotFoundException)
            {
                th = "Resource not found";
            }
            else if (ex is InvalidSessionException)
            {
                th = "Invalid session/subscription expired";
            }
            CommonTaskDialogs.Error(ex, th).Show();
        }

        private async Task DownloadQueue()
        {
#if !DEBUG
            try
            {
#endif  
                LockUi();
                currTrackLabel.Text = "Warming up...";
                currentCollection = 0;
                var downloadQueue = new Queue<DownloadableMediaCollection>(mDownloadItems);
                while (downloadQueue.Count > 0)
                {
                    var item = downloadQueue.Dequeue();
                    await DownloadTracks(item.Service, (from track in item.Tracks where track.WillDownload select track).ToList());
                    currentCollection++;
                }
#if !DEBUG
            }
            catch (Exception ex)
            {
                PresentException(ex);
            }
            finally
            {
#endif
                UnlockUi();
#if !DEBUG
            }
#endif

        }

        private async Task DownloadTracks(Service svc, List<DownloadableTrack> tracks)
        {
            var tagger = new TrackTagger();
            var downloader = new TrackDownloader(svc, tracks, mPathFormat);
            totalProgressBar.Value = 0;
            PrepareForNextTrack(tracks[0].CommonTrack, 0, tracks.Count);
            downloader.ItemProgressChanged += (o, args) =>
            {
                switch (args.CurrentTrack.State)
                {
                    case TrackState.DownloadingArtwork:
                        totalProgressStatus.Text = "Getting artwork...";
                        break;
                    case TrackState.DownloadingTrack:
                        totalProgressStatus.Text = "Getting track...";
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
                SetGlobalProgress(args.OverallCompletionPercentage);
            };
            downloader.ItemDownloadCompleted += (o, args) =>
            {
                totalProgressStatus.Text = "Tagging...";
                tagger.Write(args.CurrentTrack.Path, tracks[args.CurrentItemIndex].CommonTrack, args.CurrentTrack.ArtworkPath);
                var nextIndex = args.CurrentItemIndex + 1;
                if (nextIndex < args.TotalItems)
                {
                    PrepareForNextTrack(tracks[nextIndex].CommonTrack, nextIndex, args.TotalItems);
                }
            };
            await downloader.DownloadAsync();
            currTrackLabel.Text = GetCompletionMessage();
            totalProgressStatus.Text = "Downloaded album successfully";
        }

        #region Validation for URL
        private const string UrlInvalid = "Invalid URL. Check that the URL begins with \"http://\" or \"https://\".";
        private const string UrlNoService = "Can't download this URL.";
        private const string UrlNeedsAuthentication = "You need to sign in to {0} first. " + UrlNeedsAuthenticationLink1;
        private const string UrlNeedsAuthenticationLink1 = "Click here to sign in.";
        private const string UrlNotParseable = "The URL does not point to a valid track, album, artist or playlist.";
        private const string UrlValidParseResult = "{0} from {1}";

        private bool ValidateEnteredUrl()
        {
            urlValidStateLabel.ResetText();
            urlValidStateLabel.Links.Clear();
            urlValidStateLabel.Image = Resources.error;
            dlButton.Enabled = false;

            // Hide on empty
            if (String.IsNullOrWhiteSpace(idTextBox.Text))
            {
                urlValidStateLabel.Visible = false;
                return false;
            }
            urlValidStateLabel.Visible = true;

            Uri url;
            // Invalid URL
            if (!Uri.TryCreate(idTextBox.Text, UriKind.Absolute, out url))
            {
                urlValidStateLabel.Text = UrlInvalid;
                return false;
            }
            var service = ServiceCollection.Default.GetByHost(url.Host);
            // No service associated with host
            if (service == null)
            {
                urlValidStateLabel.Text = UrlNoService;
                return false;
            }
            // Not authenticated
            if (!service.IsAuthenticated)
            {
                urlValidStateLabel.Text = String.Format(UrlNeedsAuthentication, service.Name);
                var linkIndex = urlValidStateLabel.Text.LastIndexOf(UrlNeedsAuthenticationLink1, StringComparison.Ordinal);
                urlValidStateLabel.Links.Add(linkIndex, urlValidStateLabel.Text.Length, service);
                return false;
            }
            // URL doesn't point to media
            var result = service.ParseUrl(url);
            if (result == null)
            {
                urlValidStateLabel.Text = UrlNotParseable;
                return false;
            }
            // Success
            urlValidStateLabel.Image = Resources.done;
            urlValidStateLabel.Text = String.Format(UrlValidParseResult, result.Type, service.Name);
            dlButton.Enabled = true;
            mResult = result;
            mService = service;
            return true;
        }
        #endregion

        private async void button1_Click(object sender, EventArgs e)
        {
            SetGlobalProgress(0);
            SetGlobalProgressState(ProgressBarState.Normal);
#if !DEBUG
            try
            {
#endif
                var isAlreadyInQueue = (from item in mDownloadItems.ToArray()
                                           where item.Id == mResult.Id
                                           select item.Id).Any();
                if (isAlreadyInQueue)
                {
                    MessageBox.Show("This item is already in the download queue.", "Cannot add to queue", MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }
                
                switch (mResult.Type)
                {
                    case MediaType.Album:
                        LockUi();
                        // Get album and display it in listview
                        currTrackLabel.Text = "Getting album...";
                        var album = await mService.GetAlbumWithTracksAsync(mResult.Id);
                        AddToQueue(new DownloadableMediaCollection(mPathFormat, album.Tracks)
                        {
                            Service = mService, 
                            Id = mResult.Id,
                            CollectionType = mResult.Type,
                            Name = album.Title
                        });
                        break;

                    default:
                        MessageBox.Show(String.Format("{0}s are not supported yet.", mResult.Type), "URL Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                }
                idTextBox.Clear();
#if !DEBUG
            }
            catch (Exception ex)
            {
                PresentException(ex);
            }
            finally
            {
#endif
                currTrackLabel.Text = "Ready";
                UnlockUi();
#if !DEBUG
            }
#endif
        }

        #region Easter egg

        private readonly string[] messages = {"Woo-hoo!", "We did it!", "Yusssss", "Alright!", "Sweet!", "Nice...."};
        private readonly Random random = new Random();

        private string GetCompletionMessage()
        {
            var messagesList = messages.ToList();
            if (DateTime.Now.DayOfWeek == DayOfWeek.Friday)
            {
                messagesList.Add("It's Friday, baby!");
            }
            return messagesList[random.Next(messagesList.Count)];
        }

        #endregion

        private void MainForm_Load(object sender, EventArgs e)
        {
            var td = CommonTaskDialogs.Wait(this, null);
            var openCt = new CancellationTokenSource();
            td.Opened += async (o, args) =>
            {
                await Task.Factory.StartNew(async () =>
                {
                    foreach (var service in ServiceCollection.Default)
                    {
                        if (service.Settings.Response == null) continue;
                        var result = false;
                        td.InstructionText = String.Format("Signing into {0}...", service.Name);
                        try
                        {
                            openCt.Token.ThrowIfCancellationRequested();
                            result = await service.RestoreSessionAsync(service.Settings.Response);
                        }
                        catch (NotImplementedException)
                        {
                            result = service.RestoreSession(service.Settings.Response);
                        }
                        finally
                        {
                            if (result)
                            {
                            }
                            else
                            {
                                MessageBox.Show(String.Format("Failed to sign in to {0}", service.Name), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }
                    }
                    td.Close();
                }, openCt.Token);

            };
            if (td.Show() == TaskDialogResult.Cancel)
            {
                openCt.Cancel(true);
            }
        }

        #region Logger
        private class FormsLogger : Logger
        {
            private readonly TextBox textBox;
            private readonly Label infoLabel;

            public FormsLogger(TextBox textBox, Label infoLabel)
            {
                this.textBox = textBox;
                this.infoLabel = infoLabel;
            }

            public override void Write(MessageLevel level, string line)
            {
                var sb = new StringBuilder();
                switch (level)
                {
                    case MessageLevel.Info:
                        sb.Append("[Info] ");
                        break;
                    case MessageLevel.Debug:
                        sb.Append("[Debug] ");
                        break;
                    case MessageLevel.Warning:
                        sb.Append("[Warning] ");
                        break;
                    case MessageLevel.Error:
                        sb.Append("[Error] ");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException("level", level, null);
                }
                sb.Append(line);
                sb.Append(Environment.NewLine);
                if (textBox.InvokeRequired)
                {
                    textBox.BeginInvoke(new Action(() =>
                    {
                        textBox.Text += (sb);
                        textBox.SelectionStart = textBox.TextLength;
                        textBox.ScrollToCaret();
                    }));
                }
                else
                {
                    textBox.Text += (sb);
                    textBox.SelectionStart = textBox.TextLength;
                    textBox.ScrollToCaret();
                }
            }
        }
        #endregion

        private void settingsButton_Click(object sender, EventArgs e)
        {
            var absLoc = settingsButton.PointToScreen(new Point(0, settingsButton.Height));
            mMenu.Show(absLoc);
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            ApplicationSettings.Default.Save();
        }

        private void idTextBox_TextChanged(object sender, EventArgs e)
        {
            ValidateEnteredUrl();
        }

        private void clearButton_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            idTextBox.Clear();
        }

        private void pasteButton_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            idTextBox.Clear();
            idTextBox.Paste();
        }

        private void urlValidStateLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            var svc = (Service) e.Link.LinkData;
            using (var cf = new CredentialsForm(svc))
            {
                var res = cf.ShowDialog(this);
                if (res != DialogResult.OK) return;
                ValidateEnteredUrl();
            }

        }

        private async void startDownloadButton_Click(object sender, EventArgs e)
        {
            await DownloadQueue();
        }

        private void queueListView_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            var indices = GetIndicesOfCollectionAndTrack(e.Index);
            if (indices == null) return;
            var item = mDownloadItems[indices.Item1].Tracks[indices.Item2];
            if (!item.CommonTrack.IsDownloadable)
            {
                e.NewValue = CheckState.Unchecked;
                SystemSounds.Beep.Play();
            }
            else
            {
                item.WillDownload = e.NewValue == CheckState.Checked;
            }
        }

        private void queueListView_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            if (!queueListView.FocusedItem.Bounds.Contains(e.Location)) return;

            var indices = GetIndicesOfCollectionAndTrack(queueListView.Items.IndexOf(queueListView.FocusedItem));
            if (indices == null) return;
            var collection = mDownloadItems[indices.Item1];
            var track = collection.Tracks[indices.Item2];
            
            queueMenu.Show(Cursor.Position);
        }

        private void removeGroupToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }

        private void showInExplorerToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }

        private void queueListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (queueListView.SelectedIndices.Count == 0) return;
            GetIndicesOfCollectionAndTrack(queueListView.SelectedIndices[0]);
        }

        private void queueListView_MouseHover(object sender, EventArgs e)
        {

        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            new AboutForm().ShowDialog(this);
        }

        private void settingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            new SettingsForm().ShowDialog(this);
        }
    }
}
