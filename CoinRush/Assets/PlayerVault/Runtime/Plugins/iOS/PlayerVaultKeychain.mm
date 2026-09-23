// The iOS half of PlayerVault's save signing. Called from KeychainKeyStore in C#.
//
// Stores small byte values as generic-password Keychain items that are never synced or backed
// up to another device (kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly). Written so it
// compiles with or without ARC.

#import <Foundation/Foundation.h>
#import <Security/Security.h>
#include <string.h>

static NSMutableDictionary* PlayerVaultQuery(const char* account)
{
    NSMutableDictionary* query = [NSMutableDictionary dictionary];
    query[(__bridge id)kSecClass] = (__bridge id)kSecClassGenericPassword;
    query[(__bridge id)kSecAttrService] = @"com.playervault.integrity";
    query[(__bridge id)kSecAttrAccount] = [NSString stringWithUTF8String:account];
    return query;
}

extern "C"
{
    // Copies the item into buffer. Returns its length, 0 if there is no such item, or a negative
    // OSStatus on failure.
    int PlayerVault_KeychainRead(const char* account, unsigned char* buffer, int capacity)
    {
        @autoreleasepool
        {
            NSMutableDictionary* query = PlayerVaultQuery(account);
            query[(__bridge id)kSecReturnData] = @YES;
            query[(__bridge id)kSecMatchLimit] = (__bridge id)kSecMatchLimitOne;

            CFTypeRef result = NULL;
            OSStatus status = SecItemCopyMatching((__bridge CFDictionaryRef)query, &result);
            if (status == errSecItemNotFound) return 0;
            if (status != errSecSuccess) return status < 0 ? (int)status : -(int)status;

            NSData* data = (__bridge NSData*)result;
            int length = (int)data.length;
            if (length > capacity)
            {
                CFRelease(result);
                return (int)errSecBufferTooSmall;
            }

            memcpy(buffer, data.bytes, (size_t)length);
            CFRelease(result);
            return length;
        }
    }

    // Creates or replaces the item. Returns 0 or an OSStatus.
    int PlayerVault_KeychainWrite(const char* account, const unsigned char* data, int length)
    {
        @autoreleasepool
        {
            NSMutableDictionary* query = PlayerVaultQuery(account);
            NSDictionary* values = @{
                (__bridge id)kSecValueData: [NSData dataWithBytes:data length:(NSUInteger)length],
                (__bridge id)kSecAttrAccessible: (__bridge id)kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            };

            OSStatus status = SecItemUpdate((__bridge CFDictionaryRef)query, (__bridge CFDictionaryRef)values);
            if (status == errSecItemNotFound)
            {
                [query addEntriesFromDictionary:values];
                status = SecItemAdd((__bridge CFDictionaryRef)query, NULL);
            }

            return (int)status;
        }
    }

    // Removes the item. Returns 0 (also when there was nothing to remove) or an OSStatus.
    int PlayerVault_KeychainDelete(const char* account)
    {
        @autoreleasepool
        {
            OSStatus status = SecItemDelete((__bridge CFDictionaryRef)PlayerVaultQuery(account));
            return status == errSecItemNotFound ? 0 : (int)status;
        }
    }
}
