namespace SwiftDrop.Merchant.Api.Application.Webhooks.Ports;

public interface IWebhookSecretProtector
{
    (byte[] Ciphertext, byte[] Nonce, byte[] Tag, int KeyVersion) Encrypt(byte[] plaintext);
    byte[] Decrypt(byte[] ciphertext, byte[] nonce, byte[] tag, int keyVersion);
    (string PlaintextSecret, byte[] Ciphertext, byte[] Nonce, byte[] Tag, int KeyVersion) GenerateAndEncrypt();
}
