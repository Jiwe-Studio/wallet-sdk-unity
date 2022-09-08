# Wallet API Documentation

The Jiwe Wallet API is designed to allow you to authenticate players and allow them to receive rewards and make purchases on their accounts.

As the developer, the game will be drawing and crediting your account for any in-game transaction by players. You can keep track of transactions through the Jiwe IO wallet dashboard for your records and to make sure you have a positive wallet balance to maintain normal functionality.

Content:

1. Getting started on Jiwe IO

   1. Registering on Jiwe IO
   2. Connecting Your Jiwe IO wallet to Your Celo Wallet

2. Adding authentication into the game

   1. How authentication works
   2. Generating game wallet ID
   3. Downloading and importing the JiweWallet SDK for Unity
   4. Integrating the game wallet ID

3. Adding purchase and reward functionality through the JiweWallet SDK for Unity

   1. How the functionality works
   2. Adding rewards
   3. Adding purchases

4. Download and play the game

5. Withdrawing to Celo or Mpesa (Kotani Pay)

* * *

1. **Getting started on Jiwe IO:**

1. Register on Jiwe IO
2. Connecting Celo Wallet to Jiwe Wallet

a) Register on Jiwe IO

1. Go to[www.jiwe.io](http://www.jiwe.io), click signup to register your account, and log in with the created account
2. Once logged in click browse games which will take you to the games library and on the panel on the left-hand side click wallet to set up your wallet.

b)Creating your Jiwe IO wallet and connecting it to your Celo wallet

1. Follow the instructions on the wallet page to connect to the Celo wallet (Alfajores Test Wallet). To test your wallet
2. Request funds from[rock@jiwe.io](mailto:rock@jiwe.io) or whatapp +254773754444 to top up your Alfajores Test Wallet, and then from Alfajores wallet you can send and recieve from your Jiwe wallet page.

* * *

**2)Adding authentication to your game:**

**How authentication works:**

![](https://lh4.googleusercontent.com/m5CsdPl_rfCqGatj-jnBH2ew7bHhB-jsALx-BhF8rwVZrdqfNrIKT5kmIT5CVXM-F71mIFj-IftC6k7h9n87Dw5i2sodFwumGpNcKL1UAup4hRoQawAYEBqr0ttiBD4wtIbH5qTmCbkMZ3HMBIm6eZG_csu-juziV9vmp76xBIasefDg7MrjfnF-qg)**  
****Steps:**

1. Generating game wallet ID API key (Each game requires a unique wallet ID)
2. Downloading and importing the JiweOAuth2 SDK and integrating the game wallet ID

1. **Generating the game wallet ID**

1. Go to your profile settings by selecting the profile button on the left panel on Jiwe IO
2. Select Apps from the top tabs, follow the instructions and generate a unique Game Wallet ID, API Key and API Secret for each game you create  

   (For security purposes a unique key is required for each game to be able to validate or invalidate each game individually.)

**B. Download and import the JiweWallet SDK to your game**

**Download the JiweWallet SDK for Unity from here (Github)**

The JiweWallet SDK for Unity allows you to authenticate players on start by redirecting to a web browser page that allows you to log in with their Jiwe IO credentials and receive a token back that will be passed to the game to give the user access to the game. Using this token the player will be able to recieve and make purchases later.

**_\*If_**_after followingthe steps below and importing the first _**_JiweOAuth2 script_**_and you receive the following_**_error:”newtonsoft namespace is missing”_**_, go to the root SDK folder and drag and drop the "Newtonsoft.Json.dll" file into your assets folder (or download it from_[_www.nuget.org_](http://www.nuget.org)_search for the newtonsoft.json file)_

- Open your game build in your editor

- Download JiweWallet SDK for Unity from here (Github)

- Unzip it into a folder

- Switch to your game in the Unity engine and create thr “JiweWallet” folder in your scripts folder, then import the JiweOAuth2 and Reward scripts into your JiweWallet folder

- In your Hierarchy module on the top right, create an empty game object and name it JiweSDK and select it

- After selecting it go to your inspector and select add component and add the JiweOAuth2 Script from the scripts folder

- Put the JiweOAuth2 script in a scene game object and configure the settings below using the inspector

  - And update the configuration details on the inspector with the appropriate links

    - _Client ID (Generated in your Jiwe profile page)_
    - _API Key (Generated in your Jiwe profile page)_
    - _App Secret (Generated in your Jiwe profile page)_
    - _GameID(Your game name)_
    - _<https://id.jiwe.io/auth>_
    - _<https://id.jiwe.io/token>_
    - _<https://id.jiwe.io/me>_

![](https://lh6.googleusercontent.com/HWLENVEwyMjBRuUdcbnfmWtpVJp_Nolg5ARH6W1xGs7dgMIQammJVKd0KLMxH1PEYv7dH18RmmIx6KYojipDRxPEVtnaYpRGhOIljY3thIn1HcesunL-43yrtxzVkvyJc6IzcdF_XXZZDo9B0xTuYeitRpRP-_1wGuu_0s-2kOEVNpDA3rDNAXNqBQ)

1. Test the functionality by running the game build, which should open a popup web browser first thing the game starts and allow you to login via your Jiwe IO credentials, and then redirect you back to the game.
2. IF the login is successful the game will start and if the login fails you will not get the access token and will get an error message on your browser.
3. The SDK is configured such that if you do not get a token the game will not start.

* * *

**3. Adding purchase and reward functionality through the JiweWallet SDK**

**3a. How the reward and purchase functionality works:**

![](https://lh3.googleusercontent.com/nxz3WieVAfGAm-ftFibi9yAX5AP_y1YgH94opxO2PMVmd9lPZXA2Z4e0hzFFs-e_M2ItXbKBdps6hxGuLJYbPMUtuzKNwf35Pi6RrYmZgfBMui_a0xA4lYyVF9JzK5SlPVLJvWnil-sys6R08kegwowMNMJnyM4va120DzuHz4GSqTM_GSZn3AtELQ)

Integrating the JiweWallet SDK, the rewards and purchase method can be called anywhere in the game by integrating the functions below, please make sure you have the correct game wallet id and enough budget for each future transaction.

**a) The rewards and purchase functionality:**

- Open your game build in your editor
- Download JiweWallet SDK from developer.jiwe.io/unity(needs hosting)
- Unzip and put the JiweWalletI script in the “JiweWallet” folder with the JiweAuth2 script

_(This SDK details how to call the rewards and purchase functionality.)_

**Adding Rewards:**

You can call the reward functionality whenever you want to reward the player for an achievement or doing something desirable within the game.

The rewards function is called by using the following method within the game:

_reward.RewardPlayer( 0.1, "ORDER_ID", "Reward for passing boss#1");_

![](https://lh4.googleusercontent.com/_wKPE6vQFGV9kkgGOHOAh-qOipbsQd6UWgt1YMJ5Da4Zf9Y7XvbOzaBGwyBVrUJqMQROGWNkaneiH68pmlHhI73Wk2HbixOpfgQv3_gDTAaGfb1w41REY-RM_PNY0udoYc0SWLPScPKqHetiI6DKFKXDGukVIP2qSoR0VHLvCnyUnfXkmyTCPcvQxg)

Variables being passed:

- 0.1 - is the amount to be rewarded
- Order_ID - is useful for the dev’s records to know which reward he has given
- “Reward for passing boss#1” - is a description of the reward

The method can be called at any time as long as it has a trigger. You have to switch to scripts (using your favorite script editor) to include the method to call the reward function.

The reward acts as debit to the game wallet which was created on the developers profile. If the game wallet is at 0 the response will be a error 400: Bad request.

**Adding Purchases:**

Similar to the rewards function, you can call the purchase functionality whenever you want the gamer to pay or purchase something from the game.

The purchase function is called by using the following method within the game:

_reward.purchase( 0.1, "ORDER_ID", "Purchasing pigs in a blanket");_

![](https://lh4.googleusercontent.com/_wKPE6vQFGV9kkgGOHOAh-qOipbsQd6UWgt1YMJ5Da4Zf9Y7XvbOzaBGwyBVrUJqMQROGWNkaneiH68pmlHhI73Wk2HbixOpfgQv3_gDTAaGfb1w41REY-RM_PNY0udoYc0SWLPScPKqHetiI6DKFKXDGukVIP2qSoR0VHLvCnyUnfXkmyTCPcvQxg)

Variables being passed:

- 0.1 - is the amount to be rewarded
- Order_ID - is useful for the dev’s records to know which reward he has given
- “Purchasing pigs in a blanket" - is a description of the purchase

The method can be called at any time as long as it has a trigger. You have to switch to scripts (using your favorite script editor) to include the method to call the purchase function.

The purchase acts as debit to the game wallet which was created on the developers profile. If the game wallet is at 0 the response will be a error 400: Bad request.

* * *

**4)Uploading your game to Jiwe IO**

The upload functionality is currently disabled, please send your game by filling this[form](https://docs.google.com/forms/d/e/1FAIpQLSdhm05d9BqPreGTqGIIFeqWZl47hhP0jOgIKTPsfDFaOVIk7Q/viewform) and informing [rock@jiwe.io](mailto:rock@jiwe.io) and copy [charles@jiwe.io](mailto:charles@jiwe.io) who will upload your game within 1 day.

Once uploaded, to play the game:

1. Navigate to[www.jiwe.io](http://www.jiwe.io)
2. Login to Jiwe IO
3. Select the game

* * *

**5)Withdrawing**

a)From Jiwe IO to Celo

b)From Celo to Mpesa using Kotani Pay

**a)Withdrawing from Jiwe IO to Celo**  
-Navigate to your wallet page and select withdraw to Celo and enter the amount to withdraw and click ok.

The amount will be transferred to your Celo wallet

**b)Transfering from Celo to Mpesa using Kotani Pay**

1. Link your Celo wallet with Kotani Pay to withdraw to Mpesa
2. Dial USSD code \*483\*354# and link your Celo wallet and Kotani Pay
3. Dial USSD code \*483\*354# and select widthraw to withdraw your funds to Mpesa

* * *
