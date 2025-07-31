# Xero Mailer Suite

#### This is the Xero Mailer Suite.  

This will connect your Xero Accounts subscription to your Microsoft 365 Tenant and give you the ability to send emails from a Shared Mailbox or Mailbox using your own domain rather than the default Xero address of "messaging-service@post.xero.com".


###### The following MUST be done in your Xero Subscription 
Go to the Xero Developer Portal - https://developer.xero.com/
Sign in with your Xero credentials for your subscription.
Click the **My App** tab in the top menu
Click the **New App** button
Name the app **Xero OAuth M365 Service**
Select **Web App**
**Company or Application URL** - Enter your companies website, this MUST start with HTTPS
**Redirect URI** Set this as the website you will deply the built files to with `/xero/callback` appended
Tick the checkbox to accept the terms
Click the **Create App** button

Click **Configuration** in the left menu
Click Copy by **Client ID** and save the value in AppSettings.json
Click **Generate Secret** and save the value in AppSettings.json
In the **Login URL** enter the website you will deploy the site to followed by `/xero/connect`
i.e. `https://yourwebsite.com/xero/connect`
Click **Save** in the top right corner

Now select **Webhooks** from the left menu
Tick the **Invoices** checkbox
In the **Delivery URL** enter the website you will deploy the site to followed by `/api/webhook/xero`
i.e. `https://yourwebsite.com/api/webhook/xero`
Click **Save** in the top right corner
Now Copy the **Webhooks Key** and save the value in AppSettings.json
Click the **Send ‘Intent to Receive’** button.
Once complete you should see the Status turn green and say **OK**

Open a new tab in your browser and navigate to the **Login URL** your configured earlier
i.e. `https://yourwebsite.com/xero/connect`
You should then be prompted with a Xero Authentication screen, select the company you want to send emails as and click **Allow Access** button
You will then be returned to your web app with a message that says it was successful
Your app is now setup and ready to use.



###### The following MUST be done in your Microsoft Entra Tenant, this will need to be completed by a Global Admin or Application Admin
Go to the Entra portal - https://entra.microsoft.com/
From the menu on the left click **App Registration**
Create a new App Registration
Call it **Xero OAuth M365 Service**
Assign the API Permissions **Mail.Send** (Application Permissions NOT Delegated Permissions)
Grant **Admin Consent**
Generate a **Client Secret**, make a note of the **Client Secret Value** BEFORE going to any other screen otherwise this will be lost
Enter the **Directory (tenant) ID**, **Application (client) ID**, **Client Secret Value** and User or SHared Mailbox address in the appsettings.json file.



## Running as a Cron Job

After publishing the project, you can schedule the invoice reminder job to run daily using cron (Linux):

```
dotnet /path/to/XeroMailerWeb/XeroMailerWeb.dll --reminderjob
```

- This will run the job every day at 9am.
- The file `_CronJobHelp.txt` is generated in the publish folder with this command for your reference.

Be sure to update `/path/to/XeroMailerWeb` to the actual path where you publish the project.




# Developer Guide

#### Common requirements:
- you have to install .NET Core 8 and use Visual Studio 2022+
- You need to clean then build from within Visual Studio.
- You can also build from powershell
```
cd /path/to/XeroMailerWeb
.\Build-XeroMailerWeb.ps1
```

<!--
// Copyright © 2025 Marc Banyard
-->
