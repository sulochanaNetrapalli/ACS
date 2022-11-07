﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace IncomingCallSample
{
    using Azure.Communication;

    /// <summary>
    /// Handling different callback events
    /// and perform operations
    /// </summary>

    using Azure.Communication.CallAutomation;
    using Microsoft.VisualBasic;
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;

    public class IncomingCallHandler
    {
        public enum CommunicationIdentifierKind
        {
            UserIdentity,
            PhoneIdentity,
            UnknownIdentity
        }

        public const string userIdentityRegex = @"8:acs:[0-9a-fA-F]{8}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{12}_[0-9a-fA-F]{8}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{12}";
        public const string phoneIdentityRegex = @"^\+\d{10,14}$";

        private CallAutomationClient callAutomationClient;
        private CallConfiguration callConfiguration;
        private CallConnection callConnection;
        private CancellationTokenSource reportCancellationTokenSource;
        private CancellationToken reportCancellationToken;

        private TaskCompletionSource<bool> callEstablishedTask;
        private TaskCompletionSource<bool> callTerminatedTask;
        private TaskCompletionSource<bool> playAudioCompletedTask;
        private TaskCompletionSource<bool> toneReceivedCompleteTask;

        public IncomingCallHandler(CallAutomationClient callAutomationClient, CallConfiguration callConfiguration)
        {
            this.callConfiguration = callConfiguration;
            this.callAutomationClient = callAutomationClient;
        }

        public async Task Report(string incomingCallContext)
        {
            reportCancellationTokenSource = new CancellationTokenSource();
            reportCancellationToken = reportCancellationTokenSource.Token;

            try
            {
                AnswerCallOptions answerCallOptions = new AnswerCallOptions(incomingCallContext,
                    new Uri(callConfiguration.AppCallbackUrl));

                // Answer Call
                var response = await callAutomationClient.AnswerCallAsync(answerCallOptions);

                Logger.LogMessage(Logger.MessageType.INFORMATION, $"AnswerCallAsync Response -----> {response.GetRawResponse()}");

                callConnection = response.Value.CallConnection;
                RegisterToCallStateChangeEvent(callConnection.CallConnectionId);

                //Wait for the call to get connected
                await callEstablishedTask.Task.ConfigureAwait(false);

                RegisterToDtmfResultEvent(callConnection.CallConnectionId);

                await StartRecognizingDtmf().ConfigureAwait(false);
                var playAudioCompleted = await playAudioCompletedTask.Task.ConfigureAwait(false);

                if (!playAudioCompleted)
                {
                    await HangupAsync().ConfigureAwait(false);
                }
                else
                {
                    var toneReceivedComplete = await toneReceivedCompleteTask.Task.ConfigureAwait(false);
                    await HangupAsync().ConfigureAwait(false);
                }

                // Wait for the call to terminate
                await callTerminatedTask.Task.ConfigureAwait(false);

            }
            catch (Exception ex)
            {
                Logger.LogMessage(Logger.MessageType.ERROR, $"Call ended unexpectedly, reason: {ex.Message}");
            }
        }

        private void RegisterToCallStateChangeEvent(string callConnectionId)
        {
            callEstablishedTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            reportCancellationToken.Register(() => callEstablishedTask.TrySetCanceled());

            callTerminatedTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            //Set the callback method for call connected
            var callConnectedNotificaiton = new NotificationCallback((callEvent) =>
            {
                Logger.LogMessage(Logger.MessageType.INFORMATION, $"Call State changed to Connected");
                EventDispatcher.Instance.Unsubscribe("CallConnected", callConnectionId);

                //Start recording
                var serverCallId = callAutomationClient.GetCallConnection(callConnectionId).GetCallConnectionProperties().Value.ServerCallId;
                StartRecordingOptions startRecordingOptions = new StartRecordingOptions(new ServerCallLocator(serverCallId));
                RecordingStateResult recordingResult = callAutomationClient.GetCallRecording().StartRecording(startRecordingOptions);
                Logger.LogMessage(Logger.MessageType.INFORMATION, $"Recording got started, recording ID : {recordingResult.RecordingId} " +
                    $", Recording State: {recordingResult.RecordingState}");

                callEstablishedTask.TrySetResult(true);
            });

            //Set the callback method for call Disconnected
            var callDisconnectedNotificaiton = new NotificationCallback((callEvent) =>
            {
                Logger.LogMessage(Logger.MessageType.INFORMATION, $"Call State changed to Disconnected");
                EventDispatcher.Instance.Unsubscribe("CallDisconnected", callConnectionId);
                reportCancellationTokenSource.Cancel();
                callTerminatedTask.SetResult(true);
            });

            //Subscribe to the call connected event
            var eventId = EventDispatcher.Instance.Subscribe("CallConnected", callConnectionId, callConnectedNotificaiton);

            //Subscribe to the call disconnected event
            var eventIdDisconnected = EventDispatcher.Instance.Subscribe("CallDisconnected", callConnectionId, callDisconnectedNotificaiton);
        }

        private async Task StartRecognizingDtmf()
        {
            if (reportCancellationToken.IsCancellationRequested)
            {
                Logger.LogMessage(Logger.MessageType.INFORMATION, "Cancellation request, PlayAudio will not be performed");
                return;
            }

            try
            {
                string audioFilePath = callConfiguration.AudioFileUrl;
                PlaySource audioFileUri = new FileSource(new Uri(audioFilePath));

                // listen to play audio events
                RegisterToPlayAudioResultEvent(callConnection.CallConnectionId);

                string targetPhoneNumber = callConfiguration.TargetParticipant;

                var identifierKind = GetIdentifierKind(targetPhoneNumber);
                CommunicationIdentifier targetParticipant = null;

                if (identifierKind == CommunicationIdentifierKind.UnknownIdentity)
                {
                    Logger.LogMessage(Logger.MessageType.INFORMATION, "Unknown identity provided. Enter valid phone number or communication user id");
                    playAudioCompletedTask.TrySetResult(false);
                    toneReceivedCompleteTask.TrySetResult(false);
                }
                else if (identifierKind == CommunicationIdentifierKind.UserIdentity)
                {
                    targetParticipant = new CommunicationUserIdentifier(targetPhoneNumber);
                }
                else if (identifierKind == CommunicationIdentifierKind.PhoneIdentity)
                {
                    targetParticipant = new PhoneNumberIdentifier(targetPhoneNumber);
                }

                //Start recognizing Dtmf Tone
                var recognizeOptions = new CallMediaRecognizeDtmfOptions(targetParticipant, 1);
                recognizeOptions.InterToneTimeout = TimeSpan.FromSeconds(5);
                recognizeOptions.InitialSilenceTimeout = TimeSpan.FromSeconds(30);
                recognizeOptions.InterruptPrompt = true;
                recognizeOptions.InterruptCallMediaOperation = true;
                recognizeOptions.Prompt = audioFileUri;
                recognizeOptions.StopTones = new List<DtmfTone> { DtmfTone.Pound };

                var resp = await callConnection.GetCallMedia().StartRecognizingAsync(recognizeOptions, reportCancellationToken);

                Logger.LogMessage(Logger.MessageType.INFORMATION, $"StartRecognizingAsync response --> " +
                $"{resp}, Id: {resp.ClientRequestId}, Status: {resp.Status}");

                //Wait for 30 secs for input
                var completedTask = await Task.WhenAny(playAudioCompletedTask.Task, Task.Delay(30 * 1000)).ConfigureAwait(false);

                if (completedTask != playAudioCompletedTask.Task)
                {
                    Logger.LogMessage(Logger.MessageType.INFORMATION, "No response from user in 30 sec, initiating hangup");
                    playAudioCompletedTask.TrySetResult(false);
                    toneReceivedCompleteTask.TrySetResult(false);
                }
            }
            catch (TaskCanceledException)
            {
                Logger.LogMessage(Logger.MessageType.ERROR, " Start recognizing with Play audio prompt for Custom message got cancelled");
            }
            catch (Exception ex)
            {
                Logger.LogMessage(Logger.MessageType.ERROR, $"Failure occured while start recognizing with Play audio prompt. Exception: {ex.Message}");
            }
        }
        private async Task HangupAsync()
        {
            if (reportCancellationToken.IsCancellationRequested)
            {
                Logger.LogMessage(Logger.MessageType.INFORMATION, "Cancellation request, Hangup will not be performed");
                return;
            }

            Logger.LogMessage(Logger.MessageType.INFORMATION, "Performing Hangup operation");
            var hangupResponse = await callConnection.HangUpAsync(true, reportCancellationToken).ConfigureAwait(false);

            Logger.LogMessage(Logger.MessageType.INFORMATION, $"HangupAsync response --> {hangupResponse}");

        }

        private void RegisterToPlayAudioResultEvent(string operationContext)
        {
            playAudioCompletedTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            reportCancellationToken.Register(() => playAudioCompletedTask.TrySetCanceled());

            var playCompletedNotification = new NotificationCallback((callEvent) =>
            {
                Task.Run(() =>
                {
                    Logger.LogMessage(Logger.MessageType.INFORMATION, $"Play audio status: Completed");
                    playAudioCompletedTask.TrySetResult(true);
                    EventDispatcher.Instance.Unsubscribe("PlayCompleted", operationContext);
                });
            });

            var playFailedNotification = new NotificationCallback((callEvent) =>
            {
                Task.Run(() =>
                {
                    Logger.LogMessage(Logger.MessageType.INFORMATION, $"Play audio status: Failed");
                    playAudioCompletedTask.TrySetResult(false);
                    EventDispatcher.Instance.Unsubscribe("PlayFailed", operationContext);
                });
            });

            var playCancelledNotification = new NotificationCallback((callEvent) =>
            {
                Task.Run(() =>
                {
                    Logger.LogMessage(Logger.MessageType.INFORMATION, $"Play audio status: Cancelled");
                    playAudioCompletedTask.TrySetResult(false);
                    EventDispatcher.Instance.Unsubscribe("PlayCancelled", operationContext);
                });
            });

            //Subscribe to event
            EventDispatcher.Instance.Subscribe("PlayCompleted", operationContext, playCompletedNotification);
            EventDispatcher.Instance.Subscribe("PlayFailed", operationContext, playFailedNotification);
            EventDispatcher.Instance.Subscribe("PlayCancelled", operationContext, playCancelledNotification);
        }

        private void RegisterToDtmfResultEvent(string callConnectionId)
        {
            toneReceivedCompleteTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var dtmfReceivedEvent = new NotificationCallback((callEvent) =>
            {
                Task.Run(() =>
                {
                    var toneReceivedEvent = (RecognizeCompleted)callEvent;

                    //if (toneReceivedEvent.CollectTonesResult.Tones.Count != 0)
                    if (toneReceivedEvent.CollectTonesResult.Tones.Count != 0)
                    {
                        Logger.LogMessage(Logger.MessageType.INFORMATION, $"Tone received --------- : {toneReceivedEvent.CollectTonesResult.Tones[0]}");
                        toneReceivedCompleteTask.TrySetResult(true);
                    }
                    else
                    {
                        toneReceivedCompleteTask.TrySetResult(false);
                    }
                    EventDispatcher.Instance.Unsubscribe("RecognizeCompleted", callConnectionId);

                    playAudioCompletedTask.TrySetResult(true);
                });
            });

            var dtmfFailedEvent = new NotificationCallback((callEvent) =>
            {
                Task.Run(() =>
                {
                    Logger.LogMessage(Logger.MessageType.INFORMATION, $"Failed to recognize any Dtmf tone");
                    toneReceivedCompleteTask.TrySetResult(false);
                    EventDispatcher.Instance.Unsubscribe("Recognizefailed", callConnectionId);
                });
            });

            //Subscribe to event
            EventDispatcher.Instance.Subscribe("RecognizeCompleted", callConnectionId, dtmfReceivedEvent);
            EventDispatcher.Instance.Subscribe("Recognizefailed", callConnectionId, dtmfFailedEvent);
        }


        private CommunicationIdentifierKind GetIdentifierKind(string participantnumber)
        {
            //checks the identity type returns as string
            return Regex.Match(participantnumber, userIdentityRegex, RegexOptions.IgnoreCase).Success ? CommunicationIdentifierKind.UserIdentity :
                   Regex.Match(participantnumber, phoneIdentityRegex, RegexOptions.IgnoreCase).Success ? CommunicationIdentifierKind.PhoneIdentity :
                   CommunicationIdentifierKind.UnknownIdentity;
        }

    }
}
