# Data Streams

Data streams are a sequence of data elements made available over time. Examples of data streams include user activity on a website, stock prices, and sensor data. 
Data streams are often unbounded, meaning that they continue indefinitely. This is in contrast to traditional data, which is often stored in a 
database and can be queried at any time.

This libary demonstrates how to use the `DataStream` class to create and manipulate data streams.


## AS2

`N2.DataStreams.A2Protocol` can send and receive AS2 messages (signed with SHA-256, encrypted with AES by default).

Send:

```csharp
using var http = AS2Send.CreateHttpClient(TimeSpan.FromSeconds(30));
var options = new AS2SendOptions
{
    Endpoint = new Uri("https://partner.example/as2"),
    From = "OUR-ID", To = "PARTNER-ID",
    SigningCertificate = AS2Certificates.LoadWithPrivateKey("our.pfx", password),
    RecipientCertificate = AS2Certificates.LoadPublic("partner.cer"),
};
await AS2Send.SendFileAsync(http, options, "order.xml", bytes);
```

Receive (host-agnostic; read the request body with a size limit and pass it in):

```csharp
var receiver = new AS2Receiver(new AS2ReceiveOptions
{
    DropDirectory = "/data/as2-in",
    LocalId = "OUR-ID",
    PartnerCertificates = new Dictionary<string, X509Certificate2> { ["PARTNER-ID"] = partnerPublicCertificate },
    RecipientCertificate = ourCertificateWithPrivateKey,
});
AS2ReceiveResult result = receiver.Process(new AS2ReceivedMessage(from, to, messageId, contentType, body));
// answer with (int)result.StatusCode and result.Reason
```

Partners are authenticated by pinning their certificate; unsigned messages are rejected unless `RequireSigned` is set to false.
Message receipts (MDN) are not implemented yet.
