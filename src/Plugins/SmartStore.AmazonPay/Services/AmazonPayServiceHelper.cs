﻿using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using AmazonPay;
using AmazonPay.CommonRequests;
using SmartStore.AmazonPay.Settings;
using SmartStore.Core.Domain.Orders;
using SmartStore.Core.Logging;
using SmartStore.Services.Common;

namespace SmartStore.AmazonPay.Services
{
	/// <summary>
	/// Helper with utilities to keep the AmazonPayService tidy.
	/// </summary>
	public partial class AmazonPayService
	{
		public static string PlatformId
		{
			get { return "A3OJ83WFYM72IY"; }
		}

		public static string LeadCode
		{
			get { return "SPEXDEAPA-SmartStore.Net-CP-DP"; }
		}

		#region Utilities

		private string GetPluginUrl(string action, bool useSsl = false)
		{
			var pluginUrl = "{0}Plugins/SmartStore.AmazonPay/AmazonPay/{1}".FormatInvariant(_services.WebHelper.GetStoreLocation(useSsl), action);
			return pluginUrl;
		}

		private void SerializeOrderAttribute(AmazonPayOrderAttribute attribute, Order order)
		{
			if (attribute != null)
			{
				var sb = new StringBuilder();
				using (var writer = new StringWriter(sb))
				{
					var serializer = new XmlSerializer(typeof(AmazonPayOrderAttribute));
					serializer.Serialize(writer, attribute);

					_genericAttributeService.SaveAttribute<string>(order, AmazonPayPlugin.SystemName + ".OrderAttribute", sb.ToString(), order.StoreId);
				}
			}
		}

		private AmazonPayOrderAttribute DeserializeOrderAttribute(Order order)
		{
			var serialized = order.GetAttribute<string>(AmazonPayPlugin.SystemName + ".OrderAttribute", _genericAttributeService, order.StoreId);

			if (!serialized.HasValue())
			{
				var attribute = new AmazonPayOrderAttribute();

				// legacy < v.1.14
				attribute.OrderReferenceId = order.GetAttribute<string>(AmazonPayPlugin.SystemName + ".OrderReferenceId", order.StoreId);

				return attribute;
			}

			using (var reader = new StringReader(serialized))
			{
				var serializer = new XmlSerializer(typeof(AmazonPayOrderAttribute));
				return (AmazonPayOrderAttribute)serializer.Deserialize(reader);
			}
		}

		private bool IsPaymentMethodActive(int storeId, bool logInactive = false)
		{
			var isActive = _paymentService.IsPaymentMethodActive(AmazonPayPlugin.SystemName, storeId);

			if (!isActive && logInactive)
			{
				LogError(null, T("Plugins.Payments.AmazonPay.PaymentMethodNotActive", _services.StoreContext.CurrentStore.Name));
			}

			return isActive;
		}

		private void AddOrderNote(AmazonPaySettings settings, Order order, AmazonPayOrderNote note, string anyString = null, bool isIpn = false)
		{
			try
			{
				if (!settings.AddOrderNotes || order == null)
					return;

				var sb = new StringBuilder();

				string[] orderNoteStrings = T("Plugins.Payments.AmazonPay.OrderNoteStrings").Text.SplitSafe(";");
				string faviconUrl = "{0}Plugins/{1}/Content/images/favicon.png".FormatWith(_services.WebHelper.GetStoreLocation(false), AmazonPayPlugin.SystemName);

				sb.AppendFormat("<img src=\"{0}\" style=\"float: left; width: 16px; height: 16px;\" />", faviconUrl);

				if (anyString.HasValue())
				{
					anyString = orderNoteStrings.SafeGet((int)note).FormatWith(anyString);
				}
				else
				{
					anyString = orderNoteStrings.SafeGet((int)note);
					anyString = anyString.Replace("{0}", "");
				}

				if (anyString.HasValue())
				{
					sb.AppendFormat("<span style=\"padding-left: 4px;\">{0}</span>", anyString);
				}

				if (isIpn)
					order.HasNewPaymentNotification = true;

				order.OrderNotes.Add(new OrderNote
				{
					Note = sb.ToString(),
					DisplayToCustomer = false,
					CreatedOnUtc = DateTime.UtcNow
				});

				_orderService.UpdateOrder(order);
			}
			catch (Exception exc)
			{
				LogError(exc);
			}
		}

		private Regions.currencyCode ConvertCurrency(string currencyCode)
		{
			switch (currencyCode.EmptyNull().ToLower())
			{
				case "usd":
					return Regions.currencyCode.USD;
				case "gbp":
					return Regions.currencyCode.GBP;
				case "jpy":
					return Regions.currencyCode.JPY;
				default:
					return Regions.currencyCode.EUR;
			}
		}

		#endregion

		private Order FindOrder(AmazonPayApiData data)
		{
			Order order = null;
			string errorId = null;

			if (data.MessageType.IsCaseInsensitiveEqual("AuthorizationNotification"))
			{
				if ((order = _orderService.GetOrderByPaymentAuthorization(AmazonPayPlugin.SystemName, data.AuthorizationId)) == null)
					errorId = "AuthorizationId {0}".FormatWith(data.AuthorizationId);
			}
			else if (data.MessageType.IsCaseInsensitiveEqual("CaptureNotification"))
			{
				if ((order = _orderService.GetOrderByPaymentCapture(AmazonPayPlugin.SystemName, data.CaptureId)) == null)
					order = _orderRepository.GetOrderByAmazonId(data.AnyAmazonId);

				if (order == null)
					errorId = "CaptureId {0}".FormatWith(data.CaptureId);
			}
			else if (data.MessageType.IsCaseInsensitiveEqual("RefundNotification"))
			{
				var attribute = _genericAttributeService.GetAttributes(AmazonPayPlugin.SystemName + ".RefundId", "Order")
					.Where(x => x.Value == data.RefundId)
					.FirstOrDefault();

				if (attribute == null || (order = _orderService.GetOrderById(attribute.EntityId)) == null)
					order = _orderRepository.GetOrderByAmazonId(data.AnyAmazonId);

				if (order == null)
					errorId = "RefundId {0}".FormatWith(data.RefundId);
			}

			if (errorId.HasValue())
			{
				Logger.Warn(T("Plugins.Payments.AmazonPay.OrderNotFound", errorId));
			}

			return order;
		}

		/// <summary>
		/// Creates an API client.
		/// </summary>
		/// <param name="settings">AmazonPay settings</param>
		/// <param name="currencyCode">Currency code of primary store currency</param>
		/// <returns>AmazonPay client</returns>
		private Client CreateClient(AmazonPaySettings settings, string currencyCode = null)
		{
			var descriptor = _pluginFinder.GetPluginDescriptorBySystemName(AmazonPayPlugin.SystemName);
			var appVersion = descriptor != null ? descriptor.Version.ToString() : "1.0";

			Regions.supportedRegions region;
			switch (settings.Marketplace.EmptyNull().ToLower())
			{
				case "us":
					region = Regions.supportedRegions.us;
					break;
				case "uk":
					region = Regions.supportedRegions.uk;
					break;
				case "jp":
					region = Regions.supportedRegions.jp;
					break;
				default:
					region = Regions.supportedRegions.de;
					break;
			}

			var config = new Configuration()
				.WithAccessKey(settings.AccessKey)
				.WithClientId(settings.ClientId)
				.WithSandbox(settings.UseSandbox)
				.WithApplicationName("SmartStore.Net " + AmazonPayPlugin.SystemName)
				.WithApplicationVersion(appVersion)
				.WithRegion(region);

			if (currencyCode.HasValue())
			{
				var currency = ConvertCurrency(currencyCode);
				config = config.WithCurrencyCode(currency);
			}

			var client = new Client(config);
			return client;
		}
	}
}