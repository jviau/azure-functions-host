﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

namespace WorkerHarness.Core.Validators
{
    /// <summary>
    /// A default implementation of IValidatorFactory interface
    /// </summary>
    public class ValidatorFactory : IValidatorFactory
    {
        internal static string InvalidValidatorTypeMessage = "An error occurs when creating a validator. The {0} validator does not exist. ";

        /// <summary>
        /// Crete IValidator object based on the validator type.
        /// </summary>
        /// <param name="validatorType"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException">throw when the given validator type is not supported</exception>
        public IValidator Create(string validatorType)
        {
            IValidator validator;

            if (string.Equals(validatorType, ValidatorTypes.Regex, StringComparison.OrdinalIgnoreCase))
            {
                validator = new RegexValidator();
            }
            else if (string.Equals(validatorType, ValidatorTypes.String, StringComparison.OrdinalIgnoreCase))
            {
                validator = new StringValidator();
            }
            else
            {
                ArgumentException ex = new(string.Format(InvalidValidatorTypeMessage, validatorType));
                ex.Data["Type"] = ValidatorExceptionType.UserInputError;
                ex.Data["Data"] = validatorType;

                throw ex;
            }

            return validator;
        }
    }
}
