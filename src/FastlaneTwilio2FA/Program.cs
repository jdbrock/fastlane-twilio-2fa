﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.TwiML.Messaging;

namespace FastlaneTwilio2FA
{
    public class Program
    {
        private static string AppleUserId;
        private static string ApplePassword;
        private static string PhoneNumber;

        private static string TwilioAccountSid;
        private static string TwilioAuthToken;

        static void Main(string[] args)
        {
            AppleUserId   = GetEnvironmentVariableOrParameter("FASTLANE_USER", args, 0);
            ApplePassword = GetEnvironmentVariableOrParameter("FASTLANE_PASSWORD", args, 1);
            PhoneNumber = GetEnvironmentVariableOrParameter("SPACESHIP_2FA_SMS_DEFAULT_PHONE_NUMBER", args, 2);

            TwilioAccountSid = GetEnvironmentVariableOrParameter("2FA_TWILIO_ACCOUNT_SID", args, 3);
            TwilioAuthToken = GetEnvironmentVariableOrParameter("2FA_TWILIO_AUTH_TOKEN", args, 4);

            if (string.IsNullOrWhiteSpace(AppleUserId) || string.IsNullOrWhiteSpace(ApplePassword) || string.IsNullOrWhiteSpace(PhoneNumber) ||
                string.IsNullOrWhiteSpace(TwilioAccountSid) || string.IsNullOrWhiteSpace(TwilioAuthToken))
            {
                Console.Error.WriteLine("Missing parameters.");
                Environment.ExitCode = -1;
                return;
            }

            TwilioClient.Init(TwilioAccountSid, TwilioAuthToken);

            RunSpaceAuth();
        }

        private static async void RunSpaceAuth()
        {
            // Run Fastlane.
            var info = new ProcessStartInfo("fastlane", $"spaceauth -u {AppleUserId}");
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;

            var process = Process.Start(info);

            // Wait 10 seconds as it'll likely have sent our 2FA code by then.
            // Saves us parsing STDIN for now.
            // TODO: Parse STDIN...
            await Task.Delay(TimeSpan.FromSeconds(10));

            // Looks like we didn't need 2FA after all.
            if (process.HasExited && process.ExitCode == 0)
                return;

            // Wait for a 2FA SMS to arrive.
            while (true)
            {
                // Get messages.
                var messages = await MessageResource.ReadAsync(
                    dateSentAfter: DateTime.UtcNow.Date.AddDays(-1),
                    from: "Apple",
                    limit: 50
                );

                // Regex for finding the code.
                var regex = new Regex("(?<code>[0-9]{6})");

                // Sort messages.
                var mostRecentMessage = messages
                    .Where(x => x.Body.Contains("Your Apple ID Verification Code", StringComparison.OrdinalIgnoreCase))
                    .Where(x => regex.IsMatch(x.Body))
                    .OrderByDescending(x => x.DateCreated)
                    .FirstOrDefault();

                // Looks like we didn't find one.
                if (mostRecentMessage == null)
                    continue;

                // We only want messages created recently.
                // Otherwise we could try using a code from an earlier request.
                // TODO: Ideally we should track these as 'used' and filter them out instead.
                if (mostRecentMessage.DateCreated.Value < DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(2)))
                    continue;

                if (string.IsNullOrWhiteSpace(mostRecentMessage.Body))
                    continue;

                var match = regex.Match(mostRecentMessage.Body);
                var code  = match.Groups["code"].Value;

                await process.StandardInput.WriteLineAsync(code);

                Environment.ExitCode = 0;
                return;
            }
        }

        private static string GetEnvironmentVariableOrParameter(string variableName, object[] args, int index)
        {
            if (args.Length < index + 1)
                return null;

            return Environment.GetEnvironmentVariable(variableName);
        }
    }
}