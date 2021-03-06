﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Remotely_Shared.Models;
using Remotely_Server.Data;
using Remotely_Server.Models;
using Remotely_Server.Services;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace Remotely_Server.API
{
    [Route("api/[controller]")]
    public class RemoteControlController : Controller
    {
        public RemoteControlController(DataService dataService, IHubContext<DeviceSocketHub> deviceHub, ApplicationConfig appConfig, SignInManager<RemotelyUser> signInManager)
        {
            DataService = dataService;
            DeviceHub = deviceHub;
            AppConfig = appConfig;
            SignInManager = signInManager;
        }

        public DataService DataService { get; }
        public IHubContext<DeviceSocketHub> DeviceHub { get; }
        public ApplicationConfig AppConfig { get; }
        public SignInManager<RemotelyUser> SignInManager { get; }

        [HttpGet("{deviceName}")]
        [Authorize]
        public async Task<IActionResult> Get(string deviceName)
        {
            return await InitiateRemoteControl(deviceName, HttpContext.User.Identity.Name);
        }

        [HttpPost]
        public async Task<IActionResult> Post([FromBody]RemoteControlRequest rcRequest)
        {
            if (!AppConfig.AllowApiLogin)
            {
                return NotFound();
            }
            var result = await SignInManager.PasswordSignInAsync(rcRequest.Email, rcRequest.Password, false, true);
            if (result.Succeeded)
            {
                DataService.WriteEvent($"API login successful for {rcRequest.Email}.");
                return await InitiateRemoteControl(rcRequest.DeviceName, rcRequest.Email);
            }
            else if (result.IsLockedOut)
            {
                DataService.WriteEvent($"API login unsuccessful due to lockout for {rcRequest.Email}.");
                return Unauthorized("Account is locked.");
            }
            else if (result.RequiresTwoFactor)
            {
                DataService.WriteEvent($"API login unsuccessful due to 2FA for {rcRequest.Email}.");
                return Unauthorized("Account requires two-factor authentication.");
            }
            DataService.WriteEvent($"API login unsuccessful due to bad attempt for {rcRequest.Email}.");
            return BadRequest();
        }

        private async Task<IActionResult> InitiateRemoteControl(string deviceName, string userName)
        {
            var remotelyUser = DataService.GetUserByName(userName);
            var targetDevice = DeviceSocketHub.ServiceConnections.FirstOrDefault(x => 
                                    x.Value.OrganizationID == remotelyUser.OrganizationID &&
                                    x.Value.DeviceName.ToLower() == deviceName.ToLower());

            if (targetDevice.Value != null)
            {
                if (DataService.DoesUserHaveAccessToDevice(targetDevice.Value.ID, remotelyUser))
                {
                    var currentUsers = RCBrowserSocketHub.OrganizationConnectionList.Count(x => x.Value.OrganizationID == remotelyUser.OrganizationID);
                    if (currentUsers >= AppConfig.RemoteControlSessionLimit)
                    {
                        return BadRequest("There are already the maximum amount of active remote control sessions for your organization.");
                    }
                    await DeviceHub.Clients.Client(targetDevice.Key).SendAsync("RemoteControl", Request.HttpContext.Connection.Id, targetDevice.Key);

                    var stopWatch = Stopwatch.StartNew();
                    while (!RCDeviceSocketHub.MachineNameToSessionIDLookup.ContainsKey(targetDevice.Value.DeviceName) && stopWatch.Elapsed.TotalSeconds < 5)
                    {
                        await Task.Delay(10);
                    }

                    if (!RCDeviceSocketHub.MachineNameToSessionIDLookup.ContainsKey(targetDevice.Value.DeviceName))
                    {
                        return StatusCode(500, "The remote control process failed to start in time on the remote device.");
                    }
                    else
                    {
                        var rcSessionID = RCDeviceSocketHub.MachineNameToSessionIDLookup[targetDevice.Value.DeviceName];
                        return Ok($"{HttpContext.Request.Scheme}://{Request.Host}/RemoteControl?clientID={rcSessionID}&serviceID={targetDevice.Key}");
                    }
                }
                else
                {
                    return Unauthorized();
                }
            }
            else
            {
                return BadRequest("The target device couldn't be found.");
            }
        }
    }
}
