﻿using UnityEngine;
using UnityEngine.TestTools;
using NUnit.Framework;
using System.Collections;
using Alps.Model;
using System.Collections.Generic;

public class MatchmoreTest
{
    public const string API_KEY = "eyJ0eXAiOiJKV1QiLCJhbGciOiJFUzI1NiJ9.eyJpc3MiOiJhbHBzIiwic3ViIjoiMzU2OGRhMWMtM2YxYS00MzdiLWFiNjYtN2JlNmU4Y2IzODg2IiwiYXVkIjpbIlB1YmxpYyJdLCJuYmYiOjE1MTg1MjEwNzMsImlhdCI6MTUxODUyMTA3MywianRpIjoiMSJ9.Jt4FtCApf5xHxwgmsT1xrZuRK53krIP886TptVn-7QRqZYpwX1RE5svrfUmn1XUcuVxWum-qwDIi_BvoVmykyg";
    public const string ENVIRONMENT = "localhost";
    public int? servicePort = 9000;
    public int? pusherPort = 9001;


    [UnityTest]
    public IEnumerator Add_device_pub_sub_and_get_match_via_poll()
    {
        var matchMore = new Matchmore(API_KEY, ENVIRONMENT, useSecuredCommunication: false, servicePort: servicePort, pusherPort: pusherPort);
        var subDevice = CreateMobileDevice(matchMore, makeMain: true);
        Subscription sub;
        Publication pub;
        SetupMatch(matchMore, subDevice, out sub, out pub);

        Match match = null;
        for (int i = 10 - 1; i >= 0; i--)
        {
            var matches = matchMore.GetMatches();
            match = matches.Find(m => m.Publication.Id == pub.Id && m.Subscription.Id == sub.Id);
            if (match != null)
            {
                break;
            }
            else
            {
                yield return new WaitForSeconds(3);
            }
        }

        Assert.IsNotNull(match);
    }

    [UnityTest]
    public IEnumerator Add_device_pub_sub_and_get_match_via_subsciption()
    {
        var matchMore = new Matchmore(API_KEY, ENVIRONMENT, useSecuredCommunication: false, servicePort: servicePort, pusherPort: pusherPort);
        var subDevice = CreateMobileDevice(matchMore, makeMain: true);
        Subscription sub;
        Publication pub;
        SetupMatch(matchMore, subDevice, out sub, out pub);

        Match match = null;

        matchMore.SubscribeMatches(matches =>
        {
            match = matches.Find(m => m.Publication.Id == pub.Id && m.Subscription.Id == sub.Id);
        });

        for (int i = 10 - 1; i >= 0; i--)
        {
            if (match != null)
            {
                break;
            }
            else
            {
                yield return new WaitForSeconds(3);
            }
        }

        Assert.IsNotNull(match);
    }

    [UnityTest]
    public IEnumerator Add_device_pub_sub_and_get_match_via_web_socket()
    {
        var matchMore = new Matchmore(API_KEY, ENVIRONMENT, useSecuredCommunication: false, servicePort: servicePort, pusherPort: pusherPort);
        var subDevice = CreateMobileDevice(matchMore, makeMain: true);

        var matches = new List<Match>();
        matchMore.SubscribeMatchesWithWS(_matches =>
        {
            matches = _matches;
        });

        Subscription sub;
        Publication pub;
        SetupMatch(matchMore, subDevice, out sub, out pub);


        Match match = null;
        for (int i = 10 - 1; i >= 0; i--)
        {
            match = matches.Find(m => m.Publication.Id == pub.Id && m.Subscription.Id == sub.Id);
            if (match != null)
            {
                break;
            }
            else
            {
                yield return new WaitForSeconds(3);
            }
        }

        Assert.IsNotNull(match);
    }

    [Test]
    public void Main_device_persistence()
    {
        var matchMore = new Matchmore(API_KEY, ENVIRONMENT, useSecuredCommunication: false, servicePort: servicePort, pusherPort: pusherPort);
        var device = CreateMobileDevice(matchMore, makeMain: true);
        matchMore.CleanUp();
        matchMore = new Matchmore(API_KEY, ENVIRONMENT, useSecuredCommunication: false);
        Assert.AreEqual(device.Id, matchMore.MainDevice.Id);
    }


    [UnityTest]
    public IEnumerator Pub_sub_persistence_and_expiration()
    {
        var matchMore = new Matchmore(API_KEY, ENVIRONMENT, useSecuredCommunication: false, servicePort: servicePort, pusherPort: pusherPort);
        var device = CreateMobileDevice(matchMore, makeMain: true);

        var sub1 = matchMore.CreateSubscription(new Subscription
        {
            Topic = "Unity",
            Duration = 1,
            Range = 100,
            Selector = "test = true and price <= 200",
            Pushers = new List<string>() { "ws" }
        });

        var sub2 = matchMore.CreateSubscription(new Subscription
        {
            Topic = "Unity",
            Duration = 5,
            Range = 100,
            Selector = "test = true and price <= 200",
            Pushers = new List<string>() { "ws" }
        });

        var persistedSub = matchMore.CreateSubscription(new Subscription
        {
            Topic = "Unity",
            Duration = 60,
            Range = 100,
            Selector = "test = true and price <= 200",
            Pushers = new List<string>() { "ws" }
        });

        var activeSubs = matchMore.ActiveSubscriptions;
        Assert.NotNull(activeSubs.Find(sub => sub.Id == sub1.Id));
        Assert.NotNull(activeSubs.Find(sub => sub.Id == sub2.Id));
        Assert.NotNull(activeSubs.Find(sub => sub.Id == persistedSub.Id));

        yield return new WaitForSeconds(3);

        activeSubs = matchMore.ActiveSubscriptions;
        Assert.Null(activeSubs.Find(sub => sub.Id == sub1.Id));
        Assert.NotNull(activeSubs.Find(sub => sub.Id == sub2.Id));
        Assert.NotNull(activeSubs.Find(sub => sub.Id == persistedSub.Id));

        yield return new WaitForSeconds(7);

        activeSubs = matchMore.ActiveSubscriptions;
        Assert.Null(activeSubs.Find(sub => sub.Id == sub1.Id));
        Assert.Null(activeSubs.Find(sub => sub.Id == sub2.Id));
        Assert.NotNull(activeSubs.Find(sub => sub.Id == persistedSub.Id));

        matchMore.CleanUp();

        matchMore = new Matchmore(API_KEY, ENVIRONMENT, useSecuredCommunication: false);

        var loadedActiveSubs = matchMore.ActiveSubscriptions;

        Assert.Null(activeSubs.Find(sub => sub.Id == sub1.Id));
        Assert.Null(activeSubs.Find(sub => sub.Id == sub2.Id));
        Assert.NotNull(activeSubs.Find(sub => sub.Id == persistedSub.Id));
    }

    private static Device CreateMobileDevice(Matchmore matchMore, bool makeMain = false)
    {
        Device mobileDevice = matchMore.CreateDevice(new MobileDevice
        {
            Name = "Mobile",
            DeviceToken = ""
        }, makeMain);

        Assert.NotNull(mobileDevice);
        Assert.NotNull(mobileDevice.Id);
        Assert.AreEqual(mobileDevice.Id, matchMore.MainDevice.Id);
        return mobileDevice;
    }


    private static void SetupMatch(Matchmore matchMore, Device subDevice, out Subscription sub, out Publication pub)
    {
        var pubDevice = matchMore.CreateDevice(new MobileDevice
        {
            Name = "Publisher"
        });


        Assert.NotNull(pubDevice);
        Assert.NotNull(pubDevice.Id);

        sub = matchMore.CreateSubscription(new Subscription
        {
            Topic = "Unity",
            Duration = 30,
            Range = 100,
            Selector = "test = true and price <= 200",
            Pushers = new List<string>() { "ws" }
        }, subDevice);
        Assert.NotNull(sub);
        Assert.NotNull(sub.Id);

        pub = matchMore.CreatePublication(new Publication
        {
            Topic = "Unity",
            Duration = 30,
            Range = 100,
            Properties = new Dictionary<string, object>(){
                {"test", true},
                {"price", 199}
            }
        }, pubDevice);
        Assert.NotNull(pub);
        Assert.NotNull(pub.Id);

        matchMore.UpdateLocation(new Location
        {
            Latitude = 54.414662,
            Longitude = 18.625498
        }, subDevice);

        matchMore.UpdateLocation(new Location
        {
            Latitude = 54.414662,
            Longitude = 18.625498
        }, pubDevice);
    }
}