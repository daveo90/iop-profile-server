﻿using Google.Protobuf;
using ProfileServerProtocol;
using Iop.Profileserver;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ProfileServerProtocolTests.Tests
{
  /// <summary>
  /// PS02007 - Register Hosting Request - Quota Exceeded
  /// https://github.com/Internet-of-People/message-protocol/blob/master/tests/PS02.md#ps02007---register-hosting-request---quota-exceeded
  /// </summary>
  public class PS02007 : ProtocolTest
  {
    public const string TestName = "PS02007";
    private static NLog.Logger log = NLog.LogManager.GetLogger("Test." + TestName);

    public override string Name { get { return TestName; } }

    /// <summary>List of test's arguments according to the specification.</summary>
    private List<ProtocolTestArgument> argumentDescriptions = new List<ProtocolTestArgument>()
    {
      new ProtocolTestArgument("Server IP", ProtocolTestArgumentType.IpAddress),
      new ProtocolTestArgument("clNonCustomer Port", ProtocolTestArgumentType.Port),
    };

    public override List<ProtocolTestArgument> ArgumentDescriptions { get { return argumentDescriptions; } }


    /// <summary>
    /// Implementation of the test itself.
    /// </summary>
    /// <returns>true if the test passes, false otherwise.</returns>
    public override async Task<bool> RunAsync()
    {
      IPAddress ServerIp = (IPAddress)ArgumentValues["Server IP"];
      int ClNonCustomerPort = (int)ArgumentValues["clNonCustomer Port"];
      log.Trace("(ServerIp:'{0}',ClNonCustomerPort:{1})", ServerIp, ClNonCustomerPort);

      bool res = false;
      Passed = false;

      ProtocolClient client1 = new ProtocolClient();
      ProtocolClient client2 = new ProtocolClient();
      try
      {
        MessageBuilder mb1 = client1.MessageBuilder;
        MessageBuilder mb2 = client2.MessageBuilder;

        // Step 1
        log.Trace("Step 1");
        await client1.ConnectAsync(ServerIp, ClNonCustomerPort, true);
        bool startConversationOk = await client1.StartConversationAsync();

        Message requestMessage = mb1.CreateRegisterHostingRequest(null);
        await client1.SendMessageAsync(requestMessage);
        Message responseMessage = await client1.ReceiveMessageAsync();

        bool idOk = responseMessage.Id == requestMessage.Id;
        bool statusOk = responseMessage.Response.Status == Status.Ok;

        // Step 1 Acceptance
        bool step1Ok = idOk && statusOk;

        log.Trace("Step 1: {0}", step1Ok ? "PASSED" : "FAILED");

        client1.CloseConnection();


        // Step 2
        log.Trace("Step 2");
        await client2.ConnectAsync(ServerIp, ClNonCustomerPort, true);
        startConversationOk = await client2.StartConversationAsync();

        requestMessage = mb2.CreateRegisterHostingRequest(null);
        await client2.SendMessageAsync(requestMessage);
        responseMessage = await client2.ReceiveMessageAsync();

        idOk = responseMessage.Id == requestMessage.Id;
        statusOk = responseMessage.Response.Status == Status.ErrorQuotaExceeded;

        // Step 2 Acceptance
        bool step2Ok = idOk && statusOk;

        log.Trace("Step 2: {0}", step1Ok ? "PASSED" : "FAILED");

        Passed = step1Ok && step2Ok;

        res = true;
      }
      catch (Exception e)
      {
        log.Error("Exception occurred: {0}", e.ToString());
      }
      if (client1 != null) client1.Dispose();
      if (client2 != null) client2.Dispose();

      log.Trace("(-):{0}", res);
      return res;
    }
  }
}
